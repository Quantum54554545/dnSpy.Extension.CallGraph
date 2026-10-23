using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class ReachableCallAnalysis {
		sealed class Call {
			public readonly Instruction Instruction;
			public readonly MethodDef Target;
			public readonly SymbolicValue[] Arguments;
			public readonly bool Potential;
			public readonly string Operation;
			public readonly SymbolicState Memory;
			public Call(Instruction instruction, MethodDef target, SymbolicValue[] arguments, SymbolicState memory, bool potential = false, string? operation = null) {
				Instruction = instruction; Target = target; Arguments = arguments; Potential = potential || memory.ExceptionalPath || memory.ConservativeContext;
				Operation = operation ?? instruction.OpCode.Name;
				Memory = new SymbolicState(); Memory.ImportMemory(memory);
			}
		}
		sealed class Trace {
			public readonly List<Call> Calls = new List<Call>();
			public readonly HashSet<uint> IndirectSites = new HashSet<uint>();
			public readonly HashSet<uint> ResolvedSites = new HashSet<uint>();
			public readonly HashSet<uint> UnresolvedSites = new HashSet<uint>();
			public readonly HashSet<uint> Reached = new HashSet<uint>();
		}

		readonly HashSet<ModuleDef> modules;
		readonly Dictionary<IMethod, MethodDef?> resolved = new Dictionary<IMethod, MethodDef?>();
		readonly PointerFlowCatalog storage = new PointerFlowCatalog();
		readonly HashSet<TypeDef> initialized = new HashSet<TypeDef>();
		readonly ContextCallEvaluator evaluator;
		readonly MethodFlowRunner runner;
		readonly InvocationModel invocations;
		readonly CancellationToken cancellation_token;
		readonly Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites = new Dictionary<(MethodDef, MethodDef), CallSiteAccumulator>();
		readonly Dictionary<(MethodDef, uint, GraphDiagnosticKind), GraphDiagnostic> diagnostics = new Dictionary<(MethodDef, uint, GraphDiagnosticKind), GraphDiagnostic>();
		readonly List<ReflectionTarget> reflection_sites = new List<ReflectionTarget>();
		readonly List<VirtualCallSite> virtual_sites = new List<VirtualCallSite>();
		internal IReadOnlyList<GraphDiagnostic> Diagnostics => diagnostics.Values.ToArray();
		void Report(MethodDef method, uint offset, GraphDiagnosticKind kind, string detail, MethodDef? target = null) => diagnostics[(method, offset, kind)] = new GraphDiagnostic(method, offset, kind, detail, target);
		readonly Dictionary<MethodDef, HashSet<uint>> indirect_sites = new Dictionary<MethodDef, HashSet<uint>>();
		readonly Dictionary<MethodDef, HashSet<uint>> resolved_sites = new Dictionary<MethodDef, HashSet<uint>>();
		readonly HashSet<(MethodDef Method, uint Offset)> unknown_sites = new HashSet<(MethodDef, uint)>();
		readonly Dictionary<MethodDef, MethodDef[]> virtual_targets = new Dictionary<MethodDef, MethodDef[]>();
		readonly HashSet<IMethod> fallback_attempted = new HashSet<IMethod>();
		Dictionary<string, TypeDef[]>? loaded_types;
		HashSet<MethodDef>? workspace_methods;
		int remaining_steps = 300000;
		int remaining_helper_steps = 750000;
		int method_steps, method_budget;
		int helper_steps, helper_budget;
		int remaining_structural_instructions = 2000000;
		readonly Dictionary<MethodDef, Instruction[]> structural_calls = new Dictionary<MethodDef, Instruction[]>();
		readonly Dictionary<MethodDef, HashSet<Instruction>> continuation_calls = new Dictionary<MethodDef, HashSet<Instruction>>();
		bool incomplete;
		bool initial_state_known;
		internal Action<MethodDef, long, int>? WalkCompleted { get; set; }
		Dictionary<FieldDef, List<(MethodDef Method, int Position)>>? field_writers;
		readonly HashSet<FieldDef> inferred_fields = new HashSet<FieldDef>();

		public ReachableCallAnalysis(IEnumerable<ModuleDef> modules, CancellationToken cancellation_token) {
			this.modules = new HashSet<ModuleDef>(modules);
			this.cancellation_token = cancellation_token;
			evaluator = new ContextCallEvaluator(resolved, cancellation_token, FieldValue, storage.ElementValue, Resolve, storage);
			runner = new MethodFlowRunner(cancellation_token);
			invocations = new InvocationModel(this.modules, Resolve, (method, arguments, state) => evaluator.Evaluate(method, arguments, state));
			evaluator.Invocations = invocations;
			evaluator.ConsumeStep = ConsumeHelperStep;
			evaluator.RequestBudget = requested => helper_budget = Math.Max(helper_budget, requested);
			evaluator.CanAnalyze = method => this.modules.Contains(method.Module);
		}
		bool ConsumeStep() => --remaining_steps >= 0 && ++method_steps <= method_budget;
		bool ConsumeHelperStep() => --remaining_helper_steps >= 0 && ++helper_steps <= helper_budget;
		internal int ScanIndex(IReadOnlyList<MethodDef> methods, Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> result,
			List<VirtualCallSite> virtual_calls, List<ReflectionTarget> reflection_calls) {
			remaining_steps = int.MaxValue;
			remaining_helper_steps = int.MaxValue;
			int skipped = 0;
			foreach (var method in methods) {
				cancellation_token.ThrowIfCancellationRequested();
				if (!method.HasBody || !NeedsFlow(method)) continue;
				try {
					var trace = Walk(method, UnknownArguments(method), new SymbolicState());
					foreach (var call in trace.Calls) CallGraphIndex.AddCallSite(result, method, call.Target, call.Instruction.Offset, call.Operation, call.Potential);
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { skipped++; Report(method, 0, GraphDiagnosticKind.UnresolvedReference, error.GetType().Name + ": " + error.Message); }
			}
			virtual_calls.AddRange(virtual_sites); reflection_calls.AddRange(reflection_sites);
			return skipped;
		}
		internal static bool NeedsFlow(MethodDef method) => method.Body.ExceptionHandlers.Count != 0 || method.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Callvirt or Code.Calli or Code.Ldftn or Code.Ldvirtftn ||
			instruction.Operand is IMethod reference && (reference.Name == "Invoke" || reference.Name == "DynamicInvoke" ||
				CallGraphIndex.SafeTypeName(reference.DeclaringType).StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal) ||
				CallGraphIndex.SafeTypeName(reference.DeclaringType).StartsWith("System.Threading", StringComparison.Ordinal)));

		MethodDef? Resolve(IMethod reference) {
			var result = CallGraphIndex.Resolve(reference, resolved);
			if (result is not null || !fallback_attempted.Add(reference)) return result;
			loaded_types ??= modules.SelectMany(module => module.GetTypes()).GroupBy(type => type.FullName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var declaration = reference.DeclaringType;
			if (declaration is TypeSpec specification && specification.TypeSig is GenericInstSig instance) declaration = instance.GenericType.TypeDefOrRef;
			string name = CallGraphIndex.SafeTypeName(declaration);
			if (!loaded_types.TryGetValue(name, out var types)) return null;
			string? assembly = declaration?.DefinitionAssembly?.FullName;
			var candidates = types.Where(type => assembly is null || StringComparer.OrdinalIgnoreCase.Equals(type.Module?.Assembly?.FullName, assembly))
				.SelectMany(type => type.Methods).Where(method => method.Name == reference.Name && new SigComparer().Equals(method.MethodSig, reference.MethodSig)).Take(2).ToArray();
			if (candidates.Length == 1) resolved[reference] = result = candidates[0];
			return result;
		}

		SymbolicValue FieldValue(FieldDef? field) {
			if (field?.Constant?.Value is int literal) return SymbolicValue.FromInteger(evaluator.Integers.Constant(literal));
			if (field?.DeclaringType is TypeDef type && modules.Contains(type.Module) && initialized.Add(type)) {
				var initializer = type.FindStaticConstructor();
				if (initial_state_known || initializer?.HasBody == true) foreach (var item in type.Fields.Where(item => item.IsStatic && !item.IsLiteral)) storage.MergeField(item, SymbolicValue.FromInteger(evaluator.Integers.Constant(0, item.FieldSig?.Type.ElementType is ElementType.I8 or ElementType.U8)));
				if (initializer?.HasBody == true) {
					var state = new SymbolicState();
					evaluator.Evaluate(initializer, Array.Empty<SymbolicValue>(), state);
					if (state.ConservativeContext || evaluator.LastFailure is not null) foreach (var item in type.Fields.Where(item => item.IsStatic && !item.IsLiteral && !state.Memory.ContainsKey(item))) storage.SetField(item, SymbolicValue.UnknownBecause("type initializer effects are incomplete"));
					foreach (var pair in state.Memory) { if (pair.Key is FieldDef stored) storage.SetField(stored, pair.Value); else storage.SetSlot(pair.Key, pair.Value); }
				}
			}
			var value = storage.FieldValue(field);
			if (value.IsUnknown && field is not null && inferred_fields.Add(field)) {
				value = InferField(field); if (!value.IsUnknown) storage.SetField(field, value);
			}
			return value;
		}
		SymbolicValue InferField(FieldDef field) {
			var element = field.FieldSig?.Type.ElementType;
			bool is_delegate = invocations.IsDelegateType(field.FieldSig?.Type.ToTypeDefOrRef());
			if (!is_delegate && element is not ElementType.I and not ElementType.U and not ElementType.FnPtr && field.FieldSig?.Type.FullName is not "System.IntPtr" and not "System.UIntPtr") return SymbolicValue.Unknown;
			if (field_writers is null) {
				field_writers = new Dictionary<FieldDef, List<(MethodDef, int)>>();
				foreach (var method in modules.SelectMany(module => module.GetTypes()).SelectMany(type => type.Methods).Where(method => method.HasBody)) {
					cancellation_token.ThrowIfCancellationRequested();
					for (int position = 0; position < method.Body.Instructions.Count; position++) {
						var instruction = method.Body.Instructions[position];
						if (instruction.OpCode.Code != Code.Stsfld || storage.ResolveField(instruction.Operand as IField) is not FieldDef destination) continue;
						if (!field_writers.TryGetValue(destination, out var writers)) field_writers[destination] = writers = new List<(MethodDef, int)>();
						writers.Add((method, position));
					}
				}
			}
			if (!field_writers.TryGetValue(field, out var assignments) || assignments.Count == 0) return SymbolicValue.Unknown;
			var values = new List<SymbolicValue> { SymbolicValue.FromInteger(evaluator.Integers.Constant(0)) };
			foreach (var assignment in assignments) {
				var instructions = assignment.Method.Body.Instructions; int position = assignment.Position - 1;
				while (position >= 0 && instructions[position].OpCode.Code is Code.Nop or Code.Dup or Code.Conv_I or Code.Conv_U) position--;
				if (position >= 0 && instructions[position].OpCode.Code == Code.Newobj && is_delegate) position--;
				if (position < 0 || instructions[position].OpCode.Code != Code.Ldftn || instructions[position].Operand is not IMethod reference || Resolve(reference) is not MethodDef target) return SymbolicValue.Unknown;
				values.Add(is_delegate ? SymbolicValue.FromDelegate(field.FieldSig!.Type.FullName, field, SymbolicValue.FromMethod(target)).WithReceiver(target.IsStatic ? null : SymbolicValue.Unknown) : SymbolicValue.FromMethod(target));
			}
			return SymbolicValue.Merge(values);
		}

		public CallGraphSlice Select(MethodDef root, int depth, int limit, CallGraphFilterOptions filter, IProgress<CallGraphSlice>? progress = null) {
			initial_state_known = ReferenceEquals(root.Module.EntryPoint, root);
			depth = Math.Max(1, Math.Min(depth, Math.Max(1, limit)));
			limit = Math.Max(2, limit);
			var layers = new Dictionary<MethodDef, int> { [root] = 0 };
			var contexts = new HashSet<string>();
			var queue = new Queue<(MethodDef Method, SymbolicValue[] Arguments, int Depth, SymbolicState Memory)>();
			queue.Enqueue((root, UnknownArguments(root), 0, new SymbolicState()));
			bool truncated = false;
			bool preview_sent = false;
			while (queue.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var current = queue.Dequeue();
				if (current.Depth >= depth || !modules.Contains(current.Method.Module)) continue;
				if (!contexts.Add(evaluator.ContextKey(current.Method, current.Arguments, current.Memory))) continue;
				if (contexts.Count > 8000) { incomplete = true; Report(current.Method, 0, GraphDiagnosticKind.AnalysisBudget, "graph context limit reached"); break; }
				Trace trace;
				try {
					trace = Walk(current.Method, current.Arguments, current.Memory);
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { incomplete = true; Report(current.Method, 0, GraphDiagnosticKind.UnresolvedReference, error.GetType().Name + ": " + error.Message); continue; }
				if (!indirect_sites.TryGetValue(current.Method, out var indirect)) indirect_sites[current.Method] = indirect = new HashSet<uint>();
				if (!resolved_sites.TryGetValue(current.Method, out var known)) resolved_sites[current.Method] = known = new HashSet<uint>();
				indirect.UnionWith(trace.IndirectSites); known.UnionWith(trace.ResolvedSites);
				foreach (uint offset in trace.UnresolvedSites) unknown_sites.Add((current.Method, offset));
				foreach (var call in trace.Calls)
					CallGraphIndex.AddCallSite(call_sites, current.Method, call.Target, call.Instruction.Offset,
						call.Operation, call.Potential);
				foreach (var call in trace.Calls) {
					var accumulator = call_sites[(current.Method, call.Target)];
					if (accumulator.Count < filter.MinimumCallSiteCount || !filter.Includes(root, call.Target)) continue;
					if (!layers.ContainsKey(call.Target)) {
						if (layers.Count >= limit) { truncated = true; continue; }
						layers.Add(call.Target, current.Depth + 1);
					}
					queue.Enqueue((call.Target, call.Arguments, current.Depth + 1, call.Memory));
				}
				if (!preview_sent && progress is not null) { preview_sent = true; progress.Report(Snapshot(root, layers, filter, true)); }
			}
			return Snapshot(root, layers, filter, truncated || incomplete);
		}
		CallGraphSlice Snapshot(MethodDef root, Dictionary<MethodDef, int> layers, CallGraphFilterOptions filter, bool truncated) {
			var links = call_sites.Where(pair => layers.ContainsKey(pair.Key.Source) && layers.ContainsKey(pair.Key.Target) && pair.Value.Count >= filter.MinimumCallSiteCount)
				.Select(pair => new CallGraphLink(pair.Key.Source, pair.Key.Target, pair.Value.Count,
					pair.Value.Offsets.OrderBy(value => value).ToArray(), pair.Value.Operations.ToArray(), !pair.Value.HasDirect)).ToArray();
			var nodes = layers.Select(pair => new CallGraphNode(pair.Key, pair.Value, ReferenceEquals(root, pair.Key), !modules.Contains(pair.Key.Module)))
				.OrderBy(node => node.Layer).ThenBy(node => CallGraphIndex.DisplayName(node.Method), StringComparer.Ordinal).ToArray();
			return new CallGraphSlice(nodes, links, truncated, unknown_sites.Count,
				diagnostics.Values.Where(item => layers.ContainsKey(item.Method)).OrderBy(item => item.Method.MDToken.Raw).ThenBy(item => item.Offset).ToArray());
		}

		SymbolicValue[] UnknownArguments(MethodDef method) =>
			(method.IsStatic ? Enumerable.Empty<SymbolicValue>() : new[] { SymbolicValue.FromReference(method.DeclaringType.FullName, new FlowAddress("parameter", method, -1), false) })
			.Concat(method.MethodSig.Params.Select((type, index) => type.ElementType is ElementType.I4 or ElementType.U4 ? evaluator.OpaqueInteger() :
				type.ElementType == ElementType.ByRef ? SymbolicValue.FromStorage(new FlowAddress("parameter address", method, index)) :
				type.ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SZArray or ElementType.Array ? SymbolicValue.FromReference(type.FullName, new FlowAddress("parameter", method, index), true) : SymbolicValue.Unknown)).ToArray();

		Trace Walk(MethodDef method, IReadOnlyList<SymbolicValue> arguments, SymbolicState memory) {
			long started = WalkCompleted is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(); int previous_steps = remaining_steps;
			var trace = new Trace();
			if (!method.HasBody || method.Body.Instructions.Count == 0) {
				incomplete = true;
				Report(method, 0, GraphDiagnosticKind.UnsupportedInstruction, "method has no inspectable IL body; native, runtime-generated or rewritten implementation may be required");
				return trace;
			}
			method_steps = 0; method_budget = Math.Min(32768, Math.Max(8192, method.Body.Instructions.Count * 8));
			helper_steps = 0; helper_budget = method_budget;
			string scope = evaluator.ContextKey(method, arguments);
			var initial = new SymbolicState { Frame = new FlowAddress("frame", method, scope) }; initial.ImportMemory(memory);
			for (int index = 0; index < arguments.Count; index++) initial.Arguments[index] = arguments[index];
			var result = runner.Run(method, initial, (instruction, state) => {
				trace.Reached.Add(instruction.Offset);
				var code = instruction.OpCode.Code;
				if (code is Code.Ldftn or Code.Ldvirtftn && instruction.Operand is IMethod pointer && Resolve(pointer) is MethodDef referenced) Report(method, instruction.Offset, GraphDiagnosticKind.AddressReference, code == Code.Ldftn ? "ldftn" : "ldvirtftn", referenced);
				if (code == Code.Jmp && instruction.Operand is IMethod reference && Resolve(reference) is MethodDef target) trace.Calls.Add(new Call(instruction, target, arguments.ToArray(), state));
				else if (code is Code.Call or Code.Callvirt or Code.Newobj or Code.Calli) CallInstruction(method, instruction, state, trace, scope);
				else if (!evaluator.Execute(method, instruction, state, arguments)) {
					incomplete = true;
					Report(method, instruction.Offset, GraphDiagnosticKind.UnsupportedInstruction, "unsupported IL operation: " + instruction.OpCode.Name);
					ContextCallEvaluator.Unsupported(instruction, state);
				}
			}, ConsumeStep);
			incomplete |= result.Incomplete;
			if (result.Incomplete) {
				bool invalid = result.Failure?.StartsWith("invalid IL", StringComparison.Ordinal) == true;
				if (invalid) trace.Calls.Clear();
				Report(method, 0, invalid ? GraphDiagnosticKind.InvalidIl : GraphDiagnosticKind.AnalysisBudget, result.Failure ?? "method analysis budget exhausted");
				if (!invalid) RetainStructuralCalls(method, trace);
			}
			WalkCompleted?.Invoke(method, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, previous_steps - remaining_steps);
			return trace;
		}
		void RetainStructuralCalls(MethodDef method, Trace trace) {
			if (!structural_calls.TryGetValue(method, out var instructions)) {
				if (method.Body.Instructions.Count > 200000 || method.Body.Instructions.Count > remaining_structural_instructions) { Report(method, 0, GraphDiagnosticKind.AnalysisBudget, "structural call scan limit reached"); return; }
				remaining_structural_instructions -= method.Body.Instructions.Count;
				try {
					var reached = IlReachability.Find(method, cancellation_token);
					instructions = method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp && instruction.Operand is IMethod && (reached is null || reached.Contains(instruction.Offset))).ToArray();
					structural_calls[method] = instructions;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { Report(method, 0, GraphDiagnosticKind.InvalidIl, "structural call scan failed: " + error.GetType().Name); return; }
			}
			var recorded = trace.Calls.Select(call => call.Instruction.Offset).ToHashSet();
			var unknown_memory = new SymbolicState { ConservativeContext = true };
			bool retained = false;
			foreach (var instruction in instructions) {
				cancellation_token.ThrowIfCancellationRequested();
				if (recorded.Contains(instruction.Offset) || instruction.Operand is not IMethod reference) continue;
				if (Resolve(reference) is not MethodDef target) { Report(method, instruction.Offset, GraphDiagnosticKind.UnresolvedReference, reference.FullName); continue; }
				if (instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) {
					virtual_sites.Add(new VirtualCallSite(method, target, instruction.Offset, "possible callvirt target"));
					foreach (var implementation in VirtualTargets(method, target, instruction.Offset)) trace.Calls.Add(new Call(instruction, implementation, UnknownArguments(implementation), unknown_memory, true));
				}
				if (!target.IsAbstract) trace.Calls.Add(new Call(instruction, target, UnknownArguments(target), unknown_memory, true));
				retained = true;
			}
			if (retained) Report(method, 0, GraphDiagnosticKind.AnalysisBudget, "value-flow incomplete; structurally reachable calls retained as possible targets");
		}

		void CallInstruction(MethodDef source, Instruction instruction, SymbolicState state, Trace trace, string scope) {
			bool indirect = instruction.OpCode.Code == Code.Calli;
			var reference = instruction.Operand as IMethod;
			var signature = indirect ? instruction.Operand as MethodSig : reference is null ? null : CallSignature.For(reference);
			if (signature is null) { incomplete = true; Report(source, instruction.Offset, GraphDiagnosticKind.InvalidIl, "invalid IL call signature"); state.Stack.Clear(); return; }
			var pointer = indirect ? invocations.ResolvePointer(Pop(state), state) : SymbolicValue.Unknown;
			bool constructor = instruction.OpCode.Code == Code.Newobj;
			var arguments = new SymbolicValue[CallSignature.ArgumentCount(signature, constructor)];
			for (int i = arguments.Length - 1; i >= 0; i--) arguments[i] = Pop(state);
			if (instruction.OpCode.Code == Code.Callvirt && signature.HasThis && arguments.Length != 0 && ContextCallEvaluator.IsDefinitelyNull(arguments[0])) { state.PathTerminated = true; return; }
			MethodDef? declaration = reference is null ? null : Resolve(reference);
			var targets = indirect ? pointer.Methods.Where(target => CallSignature.Compatible(pointer, target, signature)).ToArray() : declaration is null ? Array.Empty<MethodDef>() : new[] { declaration };
			bool virtual_dispatch = !indirect && instruction.OpCode.Code == Code.Callvirt && declaration?.IsVirtual == true;
			bool exact_dispatch = false;
			if (!indirect && instruction.OpCode.Code == Code.Callvirt && arguments.Length != 0 && invocations.DispatchReference(reference!, arguments[0], state) is MethodDef implementation) { targets = new[] { implementation }; exact_dispatch = true; }
			if (virtual_dispatch && !exact_dispatch && (declaration!.IsAbstract || declaration.DeclaringType.IsInterface)) targets = Array.Empty<MethodDef>();
			if (indirect) {
				trace.IndirectSites.Add(instruction.Offset);
				if (targets.Length != 0 && !pointer.MayHaveUnknownTargets) trace.ResolvedSites.Add(instruction.Offset);
				else { trace.UnresolvedSites.Add(instruction.Offset); Report(source, instruction.Offset, GraphDiagnosticKind.UnresolvedPointer, pointer.UnknownReason ?? (pointer.Methods.Count != 0 ? "function pointer signature or remaining targets could not be resolved" : "function pointer origin is not known in this context")); }
			}
			object allocation_identity = new FlowAddress("allocation", state.Frame, instruction);
			var allocation = SymbolicValue.FromObject(CallGraphIndex.SafeTypeName(reference?.DeclaringType), allocation_identity);
			foreach (var target in targets) trace.Calls.Add(new Call(instruction, target,
				constructor ? new[] { allocation }.Concat(arguments).ToArray() : arguments,
				state, virtual_dispatch && !exact_dispatch || indirect && (pointer.IsAmbiguous || pointer.Methods.Count != 1 || pointer.VirtualMethods.Contains(target))));
			if (virtual_dispatch && !exact_dispatch) {
				virtual_sites.Add(new VirtualCallSite(source, declaration!, instruction.Offset, "possible callvirt target"));
				foreach (var target in VirtualTargets(source, declaration!, instruction.Offset)) trace.Calls.Add(new Call(instruction, target, arguments, state, true));
			}
			if (indirect) foreach (var virtual_method in pointer.VirtualMethods) {
				virtual_sites.Add(new VirtualCallSite(source, virtual_method, instruction.Offset, "possible virtual calli"));
				foreach (var target in VirtualTargets(source, virtual_method, instruction.Offset)) trace.Calls.Add(new Call(instruction, target, arguments, state, true, "possible virtual calli"));
			}
			if (!indirect && reference is not null) {
				var actual_targets = invocations.Targets(reference, arguments, state).ToArray();
				foreach (var target in actual_targets) trace.Calls.Add(new Call(instruction, target.Method, target.Arguments, state, target.Potential, target.Operation));
				if (reference.Name.ToString() is "Invoke" or "DynamicInvoke" && signature.HasThis && arguments.Length != 0) {
					bool reflection = CallGraphIndex.SafeTypeName(reference.DeclaringType).StartsWith("System.Reflection.", StringComparison.Ordinal);
					bool callable = reflection || invocations.IsDelegate(reference) || arguments[0].DynamicNames.Count != 0;
					if (arguments[0].Lookups.Count != 0) reflection_sites.Add(new ReflectionTarget(source, arguments[0].Lookups.Select(lookup => lookup.TypeName), arguments[0].Lookups.Select(lookup => lookup.MethodName), arguments[0].Methods, instruction.Offset, "reflection invoke"));
					if (callable && (actual_targets.Length == 0 || arguments[0].MayHaveUnknownTargets)) Report(source, instruction.Offset, reflection ? GraphDiagnosticKind.UnresolvedReflection : GraphDiagnosticKind.UnresolvedDelegate, arguments[0].UnknownReason ?? "invocation target depends on unresolved values");
				}
				if (targets.Length == 0 && declaration is null && actual_targets.Length == 0 && !invocations.IsFrameworkModel(reference)) Report(source, instruction.Offset, GraphDiagnosticKind.UnresolvedReference, reference.FullName);
				if (declaration?.IsPinvokeImpl == true) Report(source, instruction.Offset, GraphDiagnosticKind.NativeBoundary, declaration.ImplMap?.Module?.Name + "!" + declaration.ImplMap?.Name, declaration);
			}
			var result = SymbolicValue.Unknown;
			bool needs_result = constructor || CallSignature.ReturnsValue(signature);
			bool needs_effects = needs_result || HasContinuation(source, instruction);
			if (!needs_effects) return;
			if (reference is null || !invocations.TryEvaluate(reference, arguments, state, allocation_identity, constructor, out result)) {
				if (constructor) {
					result = allocation;
					if (declaration is not null) evaluator.Evaluate(declaration, new[] { allocation }.Concat(arguments).ToArray(), state, instruction);
					else ContextCallEvaluator.InvalidateUnknownCall(state, arguments);
				}
				else if (indirect) result = evaluator.EvaluateIndirect(pointer, signature, arguments, state, instruction);
				else if (targets.Length == 1 && (!virtual_dispatch || exact_dispatch)) {
					result = evaluator.Evaluate(targets[0], arguments, state, instruction);
					if (result.UnknownReason is string reason) Report(source, instruction.Offset, GraphDiagnosticKind.UnsupportedInstruction, reason);
				}
				else ContextCallEvaluator.InvalidateUnknownCall(state, arguments);
			}
			if (constructor || CallSignature.ReturnsValue(signature)) state.Stack.Add(result);
		}
		bool HasContinuation(MethodDef source, Instruction instruction) {
			if (!continuation_calls.TryGetValue(source, out var calls)) {
				continuation_calls[source] = calls = new HashSet<Instruction>();
				bool continuation = false;
				for (int index = source.Body.Instructions.Count - 1; index >= 0; index--) {
					if ((index & 127) == 0) cancellation_token.ThrowIfCancellationRequested();
					var current = source.Body.Instructions[index];
					if (continuation && current.OpCode.FlowControl == FlowControl.Call) calls.Add(current);
					if (current.OpCode.Code != Code.Nop) continuation = current.OpCode.Code is not Code.Ret and not Code.Throw and not Code.Rethrow;
				}
			}
			return calls.Contains(instruction);
		}

		MethodDef[] VirtualTargets(MethodDef source, MethodDef declaration, uint offset) {
			if (virtual_targets.TryGetValue(declaration, out var targets)) return targets;
			workspace_methods ??= new HashSet<MethodDef>(modules.SelectMany(module => module.GetTypes()).SelectMany(type => type.Methods));
			var possible = new Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator>();
			WorkspaceCallResolver.AddExtendedEdges(workspace_methods, new[] { new VirtualCallSite(source, declaration, offset, "possible callvirt target") }, Array.Empty<ReflectionTarget>(), possible);
			return virtual_targets[declaration] = possible.Keys.Select(pair => pair.Target).ToArray();
		}

		static SymbolicValue Pop(SymbolicState state) {
			if (state.Stack.Count == 0) return SymbolicValue.Unknown;
			int index = state.Stack.Count - 1; var value = state.Stack[index]; state.Stack.RemoveAt(index); return value;
		}
	}
}
