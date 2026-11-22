using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class flow_suspended_exception : Exception { }
	sealed class method_flow_result {
		public symbolic_value value { get; set; } = symbolic_value.unknown;
		public symbolic_state? exit { get; set; }
		public symbolic_state? exception_exit { get; set; }
		public bool incomplete { get; set; }
		public string? failure { get; set; }
	}
	sealed class method_flow_continuation {
		internal readonly Dictionary<(int position, bool exception, int partition), symbolic_state> states = new();
		internal readonly HashSet<(int position, bool exception, int partition)> queued = new();
		internal readonly Queue<(int position, bool exception, int partition)> queue = new();
		internal readonly Dictionary<(int, bool), Dictionary<string, int>> partitions = new();
		internal readonly Dictionary<int, symbolic_value> values = new();
		internal readonly method_flow_result result = new();
		internal symbolic_state? state;
		internal int position, ambiguous_branches;
		internal bool started;
		public bool is_paused { get; internal set; }
		public bool is_complete { get; internal set; }
	}

	sealed class method_flow_runner {
		sealed class body {
			public sealed record handler(int start, int end, int entry, int? filter, ExceptionHandlerType kind);
			public readonly List<handler> handlers = new List<handler>();
			public readonly Dictionary<Instruction, int> positions;
			public readonly HashSet<int> starts = new HashSet<int> { 0 };
			public readonly int[] selectors;
			public bool invalid { get; }
			public body(MethodDef method) {
				this.positions = method.Body.Instructions.Select((instruction, index) => (instruction: instruction, index: index)).ToDictionary(pair => pair.instruction, pair => pair.index);
				this.selectors = method.Body.Instructions.Count <= 512 && method.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Calli)
					? method.Body.Instructions.Skip(1).Where(instruction => instruction.OpCode.Code == Code.Switch || instruction.OpCode.Name.StartsWith("ldelem", StringComparison.Ordinal)).Select(instruction => method_flow_runner.local_index(method.Body.Instructions[this.positions[instruction] - 1])).Where(index => index >= 0).Distinct().Take(4).ToArray() : Array.Empty<int>();
				foreach (var instruction in method.Body.Instructions) {
					if (instruction.OpCode.Code == Code.Switch && instruction.Operand is not IList<Instruction> || instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch && instruction.OpCode.Code != Code.Switch && instruction.Operand is not Instruction) this.invalid = true;
					if (instruction.Operand is Instruction target) { if (this.positions.TryGetValue(target, out int position)) this.starts.Add(position); else this.invalid = true; }
					if (instruction.Operand is IList<Instruction> targets) foreach (var item in targets) { if (this.positions.TryGetValue(item, out int position)) this.starts.Add(position); else this.invalid = true; }
				}
				foreach (var handler in method.Body.ExceptionHandlers) {
					if (handler.TryStart is null || handler.HandlerStart is null || !this.positions.TryGetValue(handler.TryStart, out int start) || !this.positions.TryGetValue(handler.HandlerStart, out int entry)) { this.invalid = true; continue; }
					int end = handler.TryEnd is null ? method.Body.Instructions.Count : this.positions.TryGetValue(handler.TryEnd, out int found) ? found : -1;
					int? filter = handler.FilterStart is null ? null : this.positions.TryGetValue(handler.FilterStart, out int filter_position) ? filter_position : -1;
					if (end <= start || filter == -1) { this.invalid = true; continue; }
					this.handlers.Add(new global::dnSpy.CallGraph.Core.method_flow_runner.body.handler(start, end, entry, filter, handler.HandlerType)); this.starts.Add(entry); if (filter.HasValue) this.starts.Add(filter.Value);
				}
			}
		}
		readonly Dictionary<MethodDef, body> bodies = new Dictionary<MethodDef, body>();
		CancellationToken cancellation_token;
		internal void set_cancellation_token(CancellationToken token) => this.cancellation_token = token;
		public method_flow_runner(CancellationToken cancellation_token) => this.cancellation_token = cancellation_token;
		public method_flow_result run(MethodDef method, symbolic_state initial, Action<Instruction, symbolic_state> execute,
			Func<bool> step, bool include_handlers = true, int maximum_ambiguous_branches = int.MaxValue, method_flow_continuation? continuation = null) {
			continuation ??= new method_flow_continuation();
			var result = continuation.result;
			if (continuation.is_complete) return result;
			if (continuation.is_paused) { result.incomplete = false; result.failure = null; continuation.is_paused = false; }
			if (!method.HasBody || method.Body.Instructions.Count == 0) return result;
			this.cancellation_token.ThrowIfCancellationRequested();
			if (method.Body.Instructions.Count > 200000 || method.Body.ExceptionHandlers.Count > 128) { result.incomplete = true; result.failure = "method instruction or exception-handler limit reached"; return result; }
			if (!this.bodies.TryGetValue(method, out var body)) this.bodies.Add(method, body = new global::dnSpy.CallGraph.Core.method_flow_runner.body(method));
			if (body.invalid) { result.incomplete = true; result.failure = "invalid IL branch target"; return result; }
			var instructions = method.Body.Instructions;
			var states = continuation.states; var queued = continuation.queued; var queue = continuation.queue;
			var partitions = continuation.partitions; var values = continuation.values;
			if (!continuation.started) { enqueue(0, initial); continuation.started = true; }
			int steps = 0;
			int ambiguous_branches = continuation.ambiguous_branches;
			int maximum = Math.Min(200000, Math.Max(8192, instructions.Count * 24));
			while (queue.Count != 0 || continuation.state is not null) {
				if (result.incomplete) return result;
				int position; symbolic_state state;
				if (continuation.state is not null) { position = continuation.position; state = continuation.state; continuation.state = null; }
				else { var entry = queue.Dequeue(); queued.Remove(entry); position = entry.position; state = states[entry].clone(); }
				while (position < instructions.Count) {
					if ((++steps & 127) == 0) this.cancellation_token.ThrowIfCancellationRequested();
					if (steps > maximum || !step()) return pause(position, state, "instruction budget exhausted");
					var instruction = instructions[position]; var code = instruction.OpCode.Code;
					instruction.CalculateStackUsage(out _, out int required);
					if (code == Code.Ret) required = !call_signature.returns_value(method.MethodSig) ? 0 : 1;
					if (required >= 0 && state.stack.Count < required) { result.incomplete = true; result.failure = "invalid IL evaluation stack underflow"; return result; }
					if (state.stack.Count > 4096 || state.memory.Count > 32768 || state.escaped_storage.Count > 32768) { result.incomplete = true; result.failure = "analysis state size limit reached"; return result; }
					if (include_handlers && body.handlers.Count != 0 && method_flow_runner.may_throw(instruction)) transfer_exceptions(position, state);
					if (result.incomplete) return pause(position, state, result.failure ?? "exception transfer budget exhausted");
					if (code == Code.Ret) {
						if (state.stack.Count != required) { result.incomplete = true; result.failure = "invalid IL stack at return"; return result; }
						var returned = !call_signature.returns_value(method.MethodSig) ? symbolic_value.unknown : method_flow_runner.pop(state);
						values[position] = values.TryGetValue(position, out var previous_return) ? symbolic_value.merge(new[] { previous_return, returned }) : returned;
						result.value = symbolic_value.merge(values.Values);
						if (result.exit is null) result.exit = state.clone(); else result.exit.merge_from(state);
						break;
					}
					if (code is Code.Throw or Code.Rethrow) {
						var exit = state.clone(); exit.stack.Clear();
						if (result.exception_exit is null) result.exception_exit = exit; else result.exception_exit.merge_from(exit);
						break;
					}
					if (code == Code.Endfinally) {
						if (state.pending_exception) {
							if (result.exception_exit is null) result.exception_exit = state.clone(); else result.exception_exit.merge_from(state);
							if (include_handlers) transfer_exceptions(position, state);
						}
						foreach (var path in state.finally_targets) { var outgoing = state.clone(); outgoing.finally_targets = System.Collections.Immutable.ImmutableHashSet<finally_continuation>.Empty; if (path.next is not null) outgoing.finally_targets = outgoing.finally_targets.Add(path.next); enqueue(path.position, outgoing); }
						break;
					}
					if (code == Code.Endfilter) {
						var accepted = method_flow_runner.pop(state).integer;
						if (accepted?.is_constant != true || !accepted.is_zero) foreach (var handler in body.handlers.Where(handler => handler.filter.HasValue && handler.filter <= position && position < handler.entry)) { var outgoing = state.clone(); outgoing.stack.Clear(); outgoing.stack.Add(symbolic_value.unknown); enqueue(handler.entry, outgoing); }
						break;
					}
					if (code == Code.Switch && instruction.Operand is IList<Instruction> targets) {
						var selector = method_flow_runner.pop(state).integer;
						if (selector?.is_constant == true) enqueue(selector.value >= 0 && selector.value < targets.Count ? body.positions[targets[(int)selector.value]] : position + 1, state);
						else {
							if (++ambiguous_branches > maximum_ambiguous_branches || targets.Count > maximum_ambiguous_branches * 2L) { result.incomplete = true; result.failure = "switch selector is not known in this context"; return result; }
							foreach (var target in targets) enqueue(body.positions[target], state); enqueue(position + 1, state);
						}
						break;
					}
					if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch) {
						var tested = state.stack.LastOrDefault();
						bool? condition = context_call_evaluator.branch(code, state);
						if (condition is null && ++ambiguous_branches > maximum_ambiguous_branches) { result.incomplete = true; result.failure = "branch condition is not known in this context"; return result; }
						if (condition != false && instruction.Operand is Instruction target) {
							var outgoing = state.clone(); if (tested is not null && code is Code.Brtrue or Code.Brtrue_S) outgoing.refine_non_null(tested); enqueue(body.positions[target], outgoing);
						}
						if (condition != true) {
							var outgoing = state.clone(); if (tested is not null && code is Code.Brfalse or Code.Brfalse_S) outgoing.refine_non_null(tested); enqueue(position + 1, outgoing);
						}
						break;
					}
					if (code == Code.Jmp) {
						if (instruction.Operand is not IMethod destination || !new SigComparer().Equals(method.MethodSig, destination.MethodSig) || state.stack.Count != 0) { result.incomplete = true; result.failure = "invalid IL jmp signature or stack"; return result; }
						if (!execute_instruction(instruction, state, position)) return result;
						var returned = !call_signature.returns_value(method.MethodSig) ? symbolic_value.unknown : method_flow_runner.pop(state);
						values[position] = values.TryGetValue(position, out var previous_return) ? symbolic_value.merge(new[] { previous_return, returned }) : returned;
						if (result.exit is null) result.exit = state.clone(); else result.exit.merge_from(state); break;
					}
					if (instruction.OpCode.FlowControl == FlowControl.Branch) {
						if (instruction.Operand is Instruction target) {
							int destination = body.positions[target];
							if (code is Code.Leave or Code.Leave_S) {
								state.stack.Clear();
								var finalizers = body.handlers.Where(handler => handler.kind == ExceptionHandlerType.Finally && handler.start <= position && position < handler.end && (destination < handler.start || destination >= handler.end)).OrderBy(handler => handler.end - handler.start).ToArray();
								if (finalizers.Length != 0) {
									var previous = state.finally_targets.Count == 0 ? new finally_continuation?[] { null } : state.finally_targets.Cast<finally_continuation?>().ToArray();
									state.finally_targets = System.Collections.Immutable.ImmutableHashSet<finally_continuation>.Empty;
									foreach (var pending in previous) { var path = new finally_continuation(destination, pending); for (int index = finalizers.Length - 1; index >= 1; index--) path = new finally_continuation(finalizers[index].entry, path); state.finally_targets = state.finally_targets.Add(path); }
									enqueue(finalizers[0].entry, state); break;
								}
							}
							enqueue(destination, state);
						}
						break;
					}
					if (!execute_instruction(instruction, state, position)) return result;
					if (state.no_return_path) break;
					if (include_handlers && body.handlers.Count != 0 && instruction.OpCode.FlowControl == FlowControl.Call) transfer_exceptions(position, state);
					if (state.path_terminated) { var exit = state.clone(); exit.stack.Clear(); if (result.exception_exit is null) result.exception_exit = exit; else result.exception_exit.merge_from(exit); break; }
					if (++position >= instructions.Count) { result.incomplete = true; result.failure = "invalid IL fall-through past method body"; return result; }
					if (body.starts.Contains(position)) { enqueue(position, state); break; }
				}
			}
			result.value = values.Count == 0 ? result.exception_exit is null ? symbolic_value.bottom : symbolic_value.unknown : symbolic_value.merge(values.Values);
			continuation.is_complete = true;
			states.Clear(); queued.Clear(); queue.Clear(); partitions.Clear(); values.Clear();
			return result;
			method_flow_result pause(int position, symbolic_state state, string failure) {
				continuation.position = position; continuation.state = state; continuation.ambiguous_branches = ambiguous_branches; continuation.is_paused = true;
				result.incomplete = true; result.failure = failure; return result;
			}
			bool execute_instruction(Instruction instruction, symbolic_state state, int position) {
				var checkpoint = instruction.OpCode.FlowControl == FlowControl.Call || instruction.OpCode.Code is Code.Jmp or Code.Ldsfld or Code.Ldsflda or Code.Stsfld ? state.clone() : null;
				try { execute(instruction, state); return true; }
				catch (flow_suspended_exception) { pause(position, checkpoint ?? state.clone(), "nested helper work slice exhausted"); return false; }
			}

			void transfer_exceptions(int position, symbolic_state state) {
				foreach (var handler in body.handlers) {
					if (position < handler.start || position >= handler.end) continue;
					if (!step()) { result.incomplete = true; result.failure = "exception transfer budget exhausted"; return; }
					var outgoing = state.clone(); outgoing.stack.Clear();
					if (handler.kind is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault) outgoing.finally_targets = System.Collections.Immutable.ImmutableHashSet<finally_continuation>.Empty;
					outgoing.path_terminated = false; outgoing.exceptional_path = true; outgoing.pending_exception = handler.kind is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault;
					if (handler.kind is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter) outgoing.stack.Add(symbolic_value.unknown);
					enqueue(handler.filter ?? handler.entry, outgoing);
				}
			}
			void enqueue(int position, symbolic_state state) {
				if (position < 0 || position >= instructions.Count) { result.incomplete = true; result.failure = "invalid IL fall-through past method body"; return; }
				int partition = 0;
				if (body.selectors.Length != 0) {
					var location = (position: position, pending_exception: state.pending_exception);
					if (!partitions.TryGetValue(location, out var shapes)) partitions[location] = shapes = new Dictionary<string, int>(StringComparer.Ordinal);
					string constant(symbolic_value value) => value.integer?.is_constant == true ? value.integer.value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
					string shape = string.Join(";", body.selectors.Select(index => constant(state.memory.TryGetValue(flow_memory.local(state, index), out var stored) ? stored : state.locals.TryGetValue(index, out var local) ? local : symbolic_value.unknown))) + ":" + string.Join(";", state.stack.TakeLast(2).Select(constant));
					if (!shapes.TryGetValue(shape, out partition) && shapes.Count < 8) { partition = shapes.Count + 1; shapes.Add(shape, partition); }
				}
				var key = (position: position, pending_exception: state.pending_exception, partition: partition);
				bool changed;
				if (!states.TryGetValue(key, out var previous)) { states[key] = state.clone(); changed = true; }
				else {
					if (previous.stack.Count != state.stack.Count) { result.incomplete = true; result.failure = "invalid IL stack depths at control-flow join"; return; }
					changed = previous.merge_from(state);
				}
				if (changed && queued.Add(key)) queue.Enqueue(key);
			}
		}
		static int local_index(Instruction instruction) => instruction.OpCode.Code switch { Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, Code.Ldloc or Code.Ldloc_S when instruction.Operand is Local local => local.Index, _ => -1 };
		internal static bool may_throw(Instruction instruction) => instruction.OpCode.FlowControl is FlowControl.Call or FlowControl.Throw || instruction.OpCode.Code is Code.Ldvirtftn or Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un or Code.Castclass or Code.Box or Code.Unbox or Code.Unbox_Any or Code.Newarr or Code.Ldlen or Code.Ldfld or Code.Stfld or Code.Ldflda or Code.Ldsfld or Code.Stsfld or Code.Ldsflda or Code.Ldobj or Code.Stobj or Code.Cpobj or Code.Initobj or Code.Cpblk or Code.Initblk or Code.Localloc or Code.Ckfinite || instruction.OpCode.Name.StartsWith("ldelem", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("stelem", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("ldind", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal) || instruction.OpCode.Name.Contains("ovf", StringComparison.Ordinal);
		static symbolic_value pop(symbolic_state state) { if (state.stack.Count == 0) return symbolic_value.unknown; int index = state.stack.Count - 1; var value = state.stack[index]; state.stack.RemoveAt(index); return value; }
	}
}
