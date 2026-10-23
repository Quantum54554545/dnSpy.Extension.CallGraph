using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class MethodFlowResult {
		public SymbolicValue Value { get; set; } = SymbolicValue.Unknown;
		public SymbolicState? Exit { get; set; }
		public SymbolicState? ExceptionExit { get; set; }
		public bool Incomplete { get; set; }
		public string? Failure { get; set; }
	}

	sealed class MethodFlowRunner {
		sealed class Body {
			public sealed record Handler(int Start, int End, int Entry, int? Filter, ExceptionHandlerType Kind);
			public readonly List<Handler> Handlers = new List<Handler>();
			public readonly Dictionary<Instruction, int> Positions;
			public readonly HashSet<int> Starts = new HashSet<int> { 0 };
			public readonly int[] Selectors;
			public bool Invalid { get; }
			public Body(MethodDef method) {
				Positions = method.Body.Instructions.Select((instruction, index) => (instruction, index)).ToDictionary(pair => pair.instruction, pair => pair.index);
				Selectors = method.Body.Instructions.Count <= 512 && method.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Calli)
					? method.Body.Instructions.Skip(1).Where(instruction => instruction.OpCode.Code == Code.Switch || instruction.OpCode.Name.StartsWith("ldelem", StringComparison.Ordinal)).Select(instruction => LocalIndex(method.Body.Instructions[Positions[instruction] - 1])).Where(index => index >= 0).Distinct().Take(4).ToArray() : Array.Empty<int>();
				foreach (var instruction in method.Body.Instructions) {
					if (instruction.OpCode.Code == Code.Switch && instruction.Operand is not IList<Instruction> || instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch && instruction.OpCode.Code != Code.Switch && instruction.Operand is not Instruction) Invalid = true;
					if (instruction.Operand is Instruction target) { if (Positions.TryGetValue(target, out int position)) Starts.Add(position); else Invalid = true; }
					if (instruction.Operand is IList<Instruction> targets) foreach (var item in targets) { if (Positions.TryGetValue(item, out int position)) Starts.Add(position); else Invalid = true; }
				}
				foreach (var handler in method.Body.ExceptionHandlers) {
					if (handler.TryStart is null || handler.HandlerStart is null || !Positions.TryGetValue(handler.TryStart, out int start) || !Positions.TryGetValue(handler.HandlerStart, out int entry)) { Invalid = true; continue; }
					int end = handler.TryEnd is null ? method.Body.Instructions.Count : Positions.TryGetValue(handler.TryEnd, out int found) ? found : -1;
					int? filter = handler.FilterStart is null ? null : Positions.TryGetValue(handler.FilterStart, out int filter_position) ? filter_position : -1;
					if (end <= start || filter == -1) { Invalid = true; continue; }
					Handlers.Add(new Handler(start, end, entry, filter, handler.HandlerType)); Starts.Add(entry); if (filter.HasValue) Starts.Add(filter.Value);
				}
			}
		}
		readonly Dictionary<MethodDef, Body> bodies = new Dictionary<MethodDef, Body>();
		readonly CancellationToken cancellation_token;
		public MethodFlowRunner(CancellationToken cancellation_token) => this.cancellation_token = cancellation_token;
		public MethodFlowResult Run(MethodDef method, SymbolicState initial, Action<Instruction, SymbolicState> execute,
			Func<bool> step, bool include_handlers = true, int maximum_ambiguous_branches = int.MaxValue) {
			var result = new MethodFlowResult();
			if (!method.HasBody || method.Body.Instructions.Count == 0) return result;
			cancellation_token.ThrowIfCancellationRequested();
			if (method.Body.Instructions.Count > 200000 || method.Body.ExceptionHandlers.Count > 128) { result.Incomplete = true; result.Failure = "method instruction or exception-handler limit reached"; return result; }
			if (!bodies.TryGetValue(method, out var body)) bodies.Add(method, body = new Body(method));
			if (body.Invalid) { result.Incomplete = true; result.Failure = "invalid IL branch target"; return result; }
			var instructions = method.Body.Instructions;
			var states = new Dictionary<(int Position, bool Exception, int Partition), SymbolicState>();
			var queued = new HashSet<(int Position, bool Exception, int Partition)>();
			var queue = new Queue<(int Position, bool Exception, int Partition)>();
			var partitions = new Dictionary<(int, bool), Dictionary<string, int>>();
			var values = new Dictionary<int, SymbolicValue>();
			Enqueue(0, initial);
			int steps = 0;
			int ambiguous_branches = 0;
			int maximum = Math.Min(200000, Math.Max(8192, instructions.Count * 24));
			while (queue.Count != 0) {
				if (result.Incomplete) return result;
				var entry = queue.Dequeue(); queued.Remove(entry); int position = entry.Position;
				var state = states[entry].Clone();
				while (position < instructions.Count) {
					if ((++steps & 127) == 0) cancellation_token.ThrowIfCancellationRequested();
					if (steps > maximum || !step()) { result.Incomplete = true; result.Failure = "instruction budget exhausted"; return result; }
					var instruction = instructions[position]; var code = instruction.OpCode.Code;
					instruction.CalculateStackUsage(out _, out int required);
					if (code == Code.Ret) required = !CallSignature.ReturnsValue(method.MethodSig) ? 0 : 1;
					if (required >= 0 && state.Stack.Count < required) { result.Incomplete = true; result.Failure = "invalid IL evaluation stack underflow"; return result; }
					if (state.Stack.Count > 4096 || state.Memory.Count > 32768 || state.EscapedStorage.Count > 32768) { result.Incomplete = true; result.Failure = "analysis state size limit reached"; return result; }
					if (include_handlers && body.Handlers.Count != 0 && MayThrow(instruction)) TransferExceptions(position, state);
					if (code == Code.Ret) {
						if (state.Stack.Count != required) { result.Incomplete = true; result.Failure = "invalid IL stack at return"; return result; }
						values[position] = !CallSignature.ReturnsValue(method.MethodSig) ? SymbolicValue.Unknown : Pop(state);
						result.Value = SymbolicValue.Merge(values.Values);
						if (result.Exit is null) result.Exit = state.Clone(); else result.Exit.MergeFrom(state);
						break;
					}
					if (code is Code.Throw or Code.Rethrow) {
						var exit = state.Clone(); exit.Stack.Clear();
						if (result.ExceptionExit is null) result.ExceptionExit = exit; else result.ExceptionExit.MergeFrom(exit);
						break;
					}
					if (code == Code.Endfinally) {
						if (state.PendingException) {
							if (result.ExceptionExit is null) result.ExceptionExit = state.Clone(); else result.ExceptionExit.MergeFrom(state);
							if (include_handlers) TransferExceptions(position, state);
						}
						foreach (var continuation in state.FinallyTargets) { var outgoing = state.Clone(); outgoing.FinallyTargets = System.Collections.Immutable.ImmutableHashSet<FinallyContinuation>.Empty; if (continuation.Next is not null) outgoing.FinallyTargets = outgoing.FinallyTargets.Add(continuation.Next); Enqueue(continuation.Position, outgoing); }
						break;
					}
					if (code == Code.Endfilter) {
						var accepted = Pop(state).Integer;
						if (accepted?.IsConstant != true || !accepted.IsZero) foreach (var handler in body.Handlers.Where(handler => handler.Filter.HasValue && handler.Filter <= position && position < handler.Entry)) { var outgoing = state.Clone(); outgoing.Stack.Clear(); outgoing.Stack.Add(SymbolicValue.Unknown); Enqueue(handler.Entry, outgoing); }
						break;
					}
					if (code == Code.Switch && instruction.Operand is IList<Instruction> targets) {
						var selector = Pop(state).Integer;
						if (selector?.IsConstant == true) Enqueue(selector.Value >= 0 && selector.Value < targets.Count ? body.Positions[targets[(int)selector.Value]] : position + 1, state);
						else {
							if (++ambiguous_branches > maximum_ambiguous_branches || targets.Count > maximum_ambiguous_branches * 2L) { result.Incomplete = true; result.Failure = "switch selector is not known in this context"; return result; }
							foreach (var target in targets) Enqueue(body.Positions[target], state); Enqueue(position + 1, state);
						}
						break;
					}
					if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch) {
						var tested = state.Stack.LastOrDefault();
						bool? condition = ContextCallEvaluator.Branch(code, state);
						if (condition is null && ++ambiguous_branches > maximum_ambiguous_branches) { result.Incomplete = true; result.Failure = "branch condition is not known in this context"; return result; }
						if (condition != false && instruction.Operand is Instruction target) {
							var outgoing = state.Clone(); if (tested is not null && code is Code.Brtrue or Code.Brtrue_S) outgoing.RefineNonNull(tested); Enqueue(body.Positions[target], outgoing);
						}
						if (condition != true) {
							var outgoing = state.Clone(); if (tested is not null && code is Code.Brfalse or Code.Brfalse_S) outgoing.RefineNonNull(tested); Enqueue(position + 1, outgoing);
						}
						break;
					}
					if (code == Code.Jmp) {
						if (instruction.Operand is not IMethod destination || !new SigComparer().Equals(method.MethodSig, destination.MethodSig) || state.Stack.Count != 0) { result.Incomplete = true; result.Failure = "invalid IL jmp signature or stack"; return result; }
						execute(instruction, state); values[position] = !CallSignature.ReturnsValue(method.MethodSig) ? SymbolicValue.Unknown : Pop(state);
						if (result.Exit is null) result.Exit = state.Clone(); else result.Exit.MergeFrom(state); break;
					}
					if (instruction.OpCode.FlowControl == FlowControl.Branch) {
						if (instruction.Operand is Instruction target) {
							int destination = body.Positions[target];
							if (code is Code.Leave or Code.Leave_S) {
								state.Stack.Clear();
								var finalizers = body.Handlers.Where(handler => handler.Kind == ExceptionHandlerType.Finally && handler.Start <= position && position < handler.End && (destination < handler.Start || destination >= handler.End)).OrderBy(handler => handler.End - handler.Start).ToArray();
								if (finalizers.Length != 0) {
									var previous = state.FinallyTargets.Count == 0 ? new FinallyContinuation?[] { null } : state.FinallyTargets.Cast<FinallyContinuation?>().ToArray();
									state.FinallyTargets = System.Collections.Immutable.ImmutableHashSet<FinallyContinuation>.Empty;
									foreach (var pending in previous) { var path = new FinallyContinuation(destination, pending); for (int index = finalizers.Length - 1; index >= 1; index--) path = new FinallyContinuation(finalizers[index].Entry, path); state.FinallyTargets = state.FinallyTargets.Add(path); }
									Enqueue(finalizers[0].Entry, state); break;
								}
							}
							Enqueue(destination, state);
						}
						break;
					}
					execute(instruction, state);
					if (include_handlers && body.Handlers.Count != 0 && instruction.OpCode.FlowControl == FlowControl.Call) TransferExceptions(position, state);
					if (state.PathTerminated) { var exit = state.Clone(); exit.Stack.Clear(); if (result.ExceptionExit is null) result.ExceptionExit = exit; else result.ExceptionExit.MergeFrom(exit); break; }
					if (++position >= instructions.Count) { result.Incomplete = true; result.Failure = "invalid IL fall-through past method body"; return result; }
					if (body.Starts.Contains(position)) { Enqueue(position, state); break; }
				}
			}
			result.Value = values.Count == 0 ? SymbolicValue.Unknown : SymbolicValue.Merge(values.Values);
			return result;

			void TransferExceptions(int position, SymbolicState state) {
				foreach (var handler in body.Handlers) {
					if (position < handler.Start || position >= handler.End) continue;
					if (!step()) { result.Incomplete = true; result.Failure = "exception transfer budget exhausted"; return; }
					var outgoing = state.Clone(); outgoing.Stack.Clear();
					if (handler.Kind is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault) outgoing.FinallyTargets = System.Collections.Immutable.ImmutableHashSet<FinallyContinuation>.Empty;
					outgoing.PathTerminated = false; outgoing.ExceptionalPath = true; outgoing.PendingException = handler.Kind is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault;
					if (handler.Kind is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter) outgoing.Stack.Add(SymbolicValue.Unknown);
					Enqueue(handler.Filter ?? handler.Entry, outgoing);
				}
			}
			void Enqueue(int position, SymbolicState state) {
				if (position < 0 || position >= instructions.Count) { result.Incomplete = true; result.Failure = "invalid IL fall-through past method body"; return; }
				int partition = 0;
				if (body.Selectors.Length != 0) {
					var location = (position, state.PendingException);
					if (!partitions.TryGetValue(location, out var shapes)) partitions[location] = shapes = new Dictionary<string, int>(StringComparer.Ordinal);
					string Constant(SymbolicValue value) => value.Integer?.IsConstant == true ? value.Integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
					string shape = string.Join(";", body.Selectors.Select(index => Constant(state.Memory.TryGetValue(FlowMemory.Local(state, index), out var stored) ? stored : state.Locals.TryGetValue(index, out var local) ? local : SymbolicValue.Unknown))) + ":" + string.Join(";", state.Stack.TakeLast(2).Select(Constant));
					if (!shapes.TryGetValue(shape, out partition) && shapes.Count < 8) { partition = shapes.Count + 1; shapes.Add(shape, partition); }
				}
				var key = (position, state.PendingException, partition);
				bool changed;
				if (!states.TryGetValue(key, out var previous)) { states[key] = state.Clone(); changed = true; }
				else {
					if (previous.Stack.Count != state.Stack.Count) { result.Incomplete = true; result.Failure = "invalid IL stack depths at control-flow join"; return; }
					changed = previous.MergeFrom(state);
				}
				if (changed && queued.Add(key)) queue.Enqueue(key);
			}
		}
		static int LocalIndex(Instruction instruction) => instruction.OpCode.Code switch { Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, Code.Ldloc or Code.Ldloc_S when instruction.Operand is Local local => local.Index, _ => -1 };
		static bool MayThrow(Instruction instruction) => instruction.OpCode.FlowControl is FlowControl.Call or FlowControl.Throw || instruction.OpCode.Code is Code.Ldvirtftn or Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un or Code.Castclass or Code.Box or Code.Unbox or Code.Unbox_Any or Code.Newarr or Code.Ldlen or Code.Ldfld or Code.Stfld or Code.Ldflda or Code.Ldsfld or Code.Stsfld or Code.Ldsflda or Code.Ldobj or Code.Stobj or Code.Cpobj or Code.Initobj or Code.Cpblk or Code.Initblk or Code.Localloc or Code.Ckfinite || instruction.OpCode.Name.StartsWith("ldelem", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("stelem", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("ldind", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal) || instruction.OpCode.Name.Contains("ovf", StringComparison.Ordinal);
		static SymbolicValue Pop(SymbolicState state) { if (state.Stack.Count == 0) return SymbolicValue.Unknown; int index = state.Stack.Count - 1; var value = state.Stack[index]; state.Stack.RemoveAt(index); return value; }
	}
}
