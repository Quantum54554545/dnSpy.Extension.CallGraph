using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class ContextCallEvaluator {
		readonly Dictionary<IMethod, MethodDef?> resolved_methods;
		readonly Dictionary<(MethodDef Method, string Arguments, object? Memory), SymbolicValue> cache = new Dictionary<(MethodDef, string, object?), SymbolicValue>();
		readonly Dictionary<object, int> identities = new Dictionary<object, int>();
		readonly HashSet<MethodDef> active = new HashSet<MethodDef>();
		readonly Dictionary<IntegerExpression, SymbolicValue> pointer_roots = new Dictionary<IntegerExpression, SymbolicValue>();
		readonly HashSet<IntegerExpression> pointer_expressions = new HashSet<IntegerExpression>();
		readonly CancellationToken cancellation_token;
		readonly Func<FieldDef?, SymbolicValue> field_value;
		readonly Func<object?, IntegerExpression?, SymbolicValue>? element_value;
		readonly Func<IMethod, MethodDef?>? method_resolver;
		readonly MethodFlowRunner runner;
		internal FlowMemory Memory { get; }
		internal InvocationModel? Invocations { get; set; }
		public string? LastFailure { get; private set; }
		internal Func<bool>? ConsumeStep { get; set; }
		internal Action<int>? RequestBudget { get; set; }
		internal Func<MethodDef, bool>? CanAnalyze { get; set; }
		readonly Dictionary<MethodDef, FieldDef[]> written_fields = new Dictionary<MethodDef, FieldDef[]>();
		readonly HashSet<MethodDef> incomplete_effects = new HashSet<MethodDef>();
		readonly Dictionary<(MethodDef, string, object?), object> frames = new Dictionary<(MethodDef, string, object?), object>();
		readonly Dictionary<MethodDef, HashSet<FieldDef>> read_fields = new Dictionary<MethodDef, HashSet<FieldDef>>();
		internal Action<MethodDef, long>? EvaluationCompleted { get; set; }
		int remaining_steps;
		int next_identity;
		public IntegerExpression.Factory Integers { get; } = new IntegerExpression.Factory();

		public ContextCallEvaluator(Dictionary<IMethod, MethodDef?> resolved_methods, CancellationToken cancellation_token,
			Func<FieldDef?, SymbolicValue> field_value, Func<object?, IntegerExpression?, SymbolicValue>? element_value = null,
			Func<IMethod, MethodDef?>? method_resolver = null, PointerFlowCatalog? catalog = null) {
			this.resolved_methods = resolved_methods;
			this.cancellation_token = cancellation_token;
			this.field_value = field_value;
			this.element_value = element_value;
			this.method_resolver = method_resolver;
			runner = new MethodFlowRunner(cancellation_token);
			Memory = new FlowMemory(catalog ?? new PointerFlowCatalog(), field_value, Integers);
		}
		public void ClearCache() => cache.Clear();
		MethodDef? Resolve(IMethod method) => method_resolver is null ? CallGraphIndex.Resolve(method, resolved_methods) : method_resolver(method);

		int Identity(object value) {
			if (!identities.TryGetValue(value, out int identity)) identities.Add(value, identity = ++next_identity);
			return identity;
		}
		public SymbolicValue OpaqueInteger() => SymbolicValue.FromInteger(Integers.Variable(++next_identity));
		string ArgumentKey(SymbolicValue value) =>
			"i" + (value.Integer is null ? 0 : Identity(value.Integer)) + ":f" + (value.Floating.HasValue ? BitConverter.DoubleToInt64Bits(value.Floating.Value).ToString("X16") : "?") + ":o" + (value.Storage is null ? 0 : Identity(value.Storage)) +
			":type-object:" + value.RepresentsType + ":objects" + string.Join(",", value.StorageCandidates.Select(Identity).OrderBy(id => id)) + ":m" + string.Join(",", value.Methods.Select(Identity).OrderBy(id => id)) +
			":v" + string.Join(",", value.VirtualMethods.Select(Identity).OrderBy(id => id)) +
			":s" + string.Join(",", value.Strings.Select(Identity).OrderBy(id => id)) +
			":t" + string.Join(",", value.TypeNames.Select(Identity).OrderBy(id => id)) + ":l" + string.Join(",", value.Lookups.Select(lookup => lookup.TypeName + "::" + lookup.MethodName)) +
			":r" + (value.Receiver is null ? 0 : Identity(value.Receiver)) + ":p" + string.Join(",", value.PointerSignatures.SelectMany(pair => pair.Value).Select(signature => signature.ToString())) + ":a" + (value.OpaqueIdentity is null ? 0 : Identity(value.OpaqueIdentity)) + ":d" + string.Join(",", value.DynamicNames) + ":" + value.IsUnknown + ":" + value.IsAmbiguous + ":" + value.IsBoxed + ":" + value.IsExactType + ":" + value.MayHaveUnknownTargets + ":" + value.MayBeNull + ":kind:" + value.MethodKind + ":members:" + string.Join(";", value.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + ArgumentKey(pair.Value)));
		internal string ContextKey(MethodDef method, IReadOnlyList<SymbolicValue> arguments) => Identity(method) + ":" + string.Join(";", arguments.Select(ArgumentKey));
		internal string ContextKey(MethodDef method, IReadOnlyList<SymbolicValue> arguments, SymbolicState memory) {
			string key = ContextKey(method, arguments) + ":conservative:" + memory.ConservativeContext;
			if (memory.Memory.IsEmpty) return key;
			if (!read_fields.TryGetValue(method, out var fields)) {
				fields = new HashSet<FieldDef>(); var pending = new Queue<MethodDef>(); var visited = new HashSet<MethodDef>(); pending.Enqueue(method);
				while (pending.Count != 0 && visited.Count < 64) {
					var current = pending.Dequeue(); if (!visited.Add(current) || !current.HasBody || CanAnalyze?.Invoke(current) == false) continue;
					foreach (var instruction in current.Body.Instructions) {
						if (instruction.Operand is IField reference) { try { if (reference.ResolveFieldDef() is FieldDef field) fields.Add(field); } catch (Exception) { } }
						if (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is IMethod called && Resolve(called) is MethodDef target && target.HasBody && CanAnalyze?.Invoke(target) != false) pending.Enqueue(target);
					}
				}
				if (pending.Count != 0) fields.UnionWith(memory.Memory.Keys.OfType<FieldDef>());
				read_fields[method] = fields;
			}
			var relevant = new HashSet<object>(); var pending_storage = new Queue<object>();
			void Visit(SymbolicValue value) {
				foreach (var storage in value.PossibleStorage) {
					if (relevant.Add(storage)) pending_storage.Enqueue(storage);
					var owner = storage is FlowAddress { Kind: "aggregate member" } member ? member.Owner : FlowMemory.AddressRoot(storage);
					if (owner is not null && relevant.Add(owner)) pending_storage.Enqueue(owner);
				}
				foreach (var member in value.Members.Values) Visit(member);
			}
			var children = memory.Memory.Where(pair => pair.Key is FlowAddress).ToLookup(pair => ((FlowAddress)pair.Key).Owner);
			foreach (var pair in memory.Memory.Where(pair => pair.Key is FieldDef field && fields.Contains(field))) { relevant.Add(pair.Key); Visit(pair.Value); }
			foreach (var argument in arguments) Visit(argument);
			while (pending_storage.Count != 0) {
				var storage = pending_storage.Dequeue(); Visit(memory.Load(storage));
				foreach (var pair in children[storage]) { relevant.Add(pair.Key); Visit(pair.Value); }
			}
			return key + ":escaped:" + string.Join(",", memory.EscapedStorage.Where(relevant.Contains).Select(Identity).OrderBy(id => id)) + ":heap:" + string.Join(";", memory.Memory.Where(pair => relevant.Contains(pair.Key)).Select(pair => (Key: Identity(pair.Key), Value: ArgumentKey(pair.Value))).OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
		}

		public SymbolicValue Evaluate(MethodDef method, IReadOnlyList<SymbolicValue> arguments, SymbolicState? caller = null, object? call_site = null) {
			if (CanAnalyze?.Invoke(method) == false) { if (caller is not null) InvalidateEffects(method, arguments, caller); return SymbolicValue.UnknownBecause("method body is outside the loaded workspace"); }
			if (method.HasBody) RequestBudget?.Invoke(Math.Min(200000, method.Body.Instructions.Count * 2));
			if (active.Count == 0) { remaining_steps = 120000; LastFailure = null; }
			if (active.Count >= 64 || !method.HasBody || remaining_steps <= 0) {
				LastFailure = !method.HasBody ? "method has no IL body" : "helper analysis budget exhausted";
				if (caller is not null) InvalidateEffects(method, arguments, caller);
				return SymbolicValue.UnknownBecause(LastFailure);
			}
			var key = (method, string.Join(";", arguments.Select(ArgumentKey)) + ":conservative:" + (caller?.ConservativeContext == true), caller is null || caller.Memory.IsEmpty ? null : caller.MemoryStamp);
			if (cache.TryGetValue(key, out var cached)) return cached;
			if (!active.Add(method)) { LastFailure = "recursive helper summary is not available"; if (caller is not null) InvalidateEffects(method, arguments, caller); return SymbolicValue.UnknownBecause(LastFailure); }
			long started = EvaluationCompleted is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
			try {
				var frame_key = (method, key.Item2, caller is null ? null : (object)new FlowAddress("callsite", caller.Frame, call_site ?? method));
				if (!frames.TryGetValue(frame_key, out var frame)) frames[frame_key] = frame = new object();
				var state = new SymbolicState { Frame = frame };
				if (caller is not null) state.ImportMemory(caller);
				for (int index = 0; index < arguments.Count; index++) state.Arguments[index] = arguments[index];
				object stamp = state.MemoryStamp;
				var flow = Run(method, arguments, state);
				var result = flow.Incomplete ? SymbolicValue.Merge(new[] { flow.Value, SymbolicValue.UnknownBecause(flow.Failure ?? "helper analysis budget exhausted") }) : flow.Value;
				if (flow.Incomplete) LastFailure = flow.Failure;
				if (caller is not null) {
					if (!flow.Incomplete && flow.Exit is not null) caller.ImportMemory(flow.Exit);
					else if (!flow.Incomplete && flow.ExceptionExit is not null) { caller.ImportMemory(flow.ExceptionExit); caller.PathTerminated = true; }
					else InvalidateEffects(method, arguments, caller);
				}
				if (!flow.Incomplete && flow.Exit is not null && ReferenceEquals(stamp, flow.Exit.MemoryStamp) && !result.IsAmbiguous && result.Storage is null && result.Receiver is null &&
					(result.Methods.Count == 1 || result.Integer?.IsConstant == true) && cache.Count < 16000) cache[key] = result;
				return result;
			}
			finally { active.Remove(method); if (EvaluationCompleted is not null) EvaluationCompleted(method, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
		}
		void InvalidateEffects(MethodDef method, IReadOnlyList<SymbolicValue> arguments, SymbolicState state) {
			if (!method.HasBody || CanAnalyze?.Invoke(method) == false) { InvalidateUnknownCall(state, arguments); return; }
			if (!written_fields.TryGetValue(method, out var fields)) {
				var collected = new HashSet<FieldDef>(); var visited = new HashSet<MethodDef>(); var queue = new Queue<MethodDef>(); queue.Enqueue(method);
				int steps = 0;
				while (queue.Count != 0 && visited.Count < 128 && steps < 16000) {
					var current = queue.Dequeue(); if (!visited.Add(current) || !current.HasBody || CanAnalyze?.Invoke(current) == false) continue;
					foreach (var instruction in current.Body.Instructions) {
						if (++steps > 16000) break;
						if (instruction.OpCode.Code is Code.Calli or Code.Cpblk or Code.Initblk or Code.Stobj or Code.Cpobj or Code.Initobj || instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal)) incomplete_effects.Add(method);
						if (instruction.OpCode.Code is Code.Stsfld or Code.Stfld && instruction.Operand is IField reference) { try { if (reference.ResolveFieldDef() is FieldDef field) collected.Add(field); } catch (Exception) { } }
						if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is IMethod called) {
							var target = Resolve(called);
							if (target is null || !target.HasBody || CanAnalyze?.Invoke(target) == false || instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) incomplete_effects.Add(method);
							else queue.Enqueue(target);
						}
					}
				}
				fields = collected.ToArray(); written_fields[method] = fields;
				if (queue.Count != 0 || steps >= 16000) incomplete_effects.Add(method);
			}
			var affected = new HashSet<FieldDef>(fields); bool all = incomplete_effects.Contains(method);
			if (all) { InvalidateUnknownCall(state, arguments); return; }
			var unknown = SymbolicValue.UnknownBecause("field may have been changed by an incomplete helper analysis");
			var argument_owners = arguments.Select(value => FlowMemory.ReadObject(state, value).Storage).Where(owner => owner is not null).ToHashSet();
			foreach (var key in state.Memory.Keys.Where(key => key is FlowAddress slot && argument_owners.Contains(slot.Owner) && (slot.Kind.StartsWith("collection ", StringComparison.Ordinal) || slot.Kind is "element" or "length")).ToArray()) state.Store(key, SymbolicValue.Merge(new[] { state.Load(key), SymbolicValue.UnknownBecause("container may have been changed by an incomplete helper analysis") }));
			foreach (var field in fields) if (field.IsStatic) state.Store(field, unknown);
			foreach (var key in state.Memory.Keys.Where(key => key is FlowAddress slot && (slot.Kind == "field" && slot.Slot is FieldDef field && (all || affected.Contains(field)) || all && slot.Kind == "element") || all && key is FieldDef).ToArray()) state.Store(key, unknown);
			int offset = method.IsStatic ? 0 : 1;
			for (int index = 0; index < method.MethodSig.Params.Count && index + offset < arguments.Count; index++) if (method.MethodSig.Params[index].ElementType == ElementType.ByRef && arguments[index + offset].Storage is object address) state.Store(address, SymbolicValue.UnknownBecause("by-reference value may have been changed by an incomplete helper analysis"));
		}

		MethodFlowResult Run(MethodDef method, IReadOnlyList<SymbolicValue> arguments, SymbolicState initial) {
			string? unsupported = null;
			var flow = runner.Run(method, initial, (instruction, state) => {
				if (instruction.OpCode.Code == Code.Jmp && instruction.Operand is IMethod reference && Resolve(reference) is MethodDef target) { state.Stack.Add(Evaluate(target, arguments, state)); return; }
				if (!Execute(method, instruction, state, arguments)) {
					LastFailure = "unsupported IL operation: " + instruction.OpCode.Name;
					unsupported ??= LastFailure;
					Unsupported(instruction, state);
				}
			}, () => --remaining_steps >= 0 && (ConsumeStep?.Invoke() ?? true), maximum_ambiguous_branches:
				method.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn) ? 32 : !CallSignature.ReturnsValue(method.MethodSig) ? 0 : 2);
			if (unsupported is not null) { flow.Incomplete = true; flow.Failure ??= unsupported; }
			return flow;
		}

		internal bool Execute(MethodDef method, Instruction instruction, SymbolicState state, IReadOnlyList<SymbolicValue> arguments) {
			var code = instruction.OpCode.Code;
			if (Memory.Execute(method, instruction, state, arguments)) return true;
			if (instruction.IsLdcI4()) { state.Stack.Add(SymbolicValue.FromInteger(Integers.Constant(instruction.GetLdcI4Value()))); return true; }
			int local = LocalIndex(instruction);
			if (local >= 0) {
				var address = FlowMemory.Local(state, local);
				if (code == Code.Stloc || code == Code.Stloc_S || code >= Code.Stloc_0 && code <= Code.Stloc_3) {
					var value = Pop(state); state.Locals[local] = value; if (state.Memory.ContainsKey(address)) state.Store(address, value);
				}
				else state.Stack.Add(state.Memory.TryGetValue(address, out var stored) ? stored : state.Locals.TryGetValue(local, out var value) ? value : local < method.Body.Variables.Count ? Memory.InitialLocal(method, method.Body.Variables[local], address) : SymbolicValue.Unknown);
				return true;
			}
			int argument = ArgumentIndex(instruction);
			if (argument >= 0) { state.Stack.Add(state.Memory.TryGetValue(FlowMemory.Argument(state, argument), out var stored) ? stored : state.Arguments.TryGetValue(argument, out var changed) ? changed : argument < arguments.Count ? arguments[argument] : SymbolicValue.Unknown); return true; }
			switch (code) {
			case Code.Nop: case Code.Volatile: case Code.Unaligned: case Code.Tailcall: case Code.Readonly: case Code.Constrained: return true;
			case Code.Ldc_I8: state.Stack.Add(SymbolicValue.FromInteger(Integers.Constant((long)instruction.Operand, true))); return true;
			case Code.Ldc_R4: state.Stack.Add(SymbolicValue.FromFloating((float)instruction.Operand)); return true;
			case Code.Ldc_R8: state.Stack.Add(SymbolicValue.FromFloating((double)instruction.Operand)); return true;
			case Code.Ldnull: state.Stack.Add(SymbolicValue.FromInteger(Integers.Constant(0))); return true;
			case Code.Ldstr: state.Stack.Add(SymbolicValue.FromString((string)instruction.Operand)); return true;
			case Code.Dup: state.Stack.Add(state.Stack.Count != 0 ? state.Stack[state.Stack.Count - 1] : SymbolicValue.Unknown); return true;
			case Code.Pop: Pop(state); return true;
			case Code.Box: state.Stack.Add(SymbolicValue.Box(Pop(state))); return true;
			case Code.Unbox_Any: state.Stack.Add(SymbolicValue.Unbox(Pop(state))); return true;
			case Code.Castclass: state.Stack.Add(Pop(state)); return true;
			case Code.Ldtoken: {
				var value = instruction.Operand is IMethod reference && Resolve(reference) is MethodDef target ? SymbolicValue.FromMethod(target, signature: CallSignature.For(reference)).AsMethodKind(MethodValueKind.Handle) : instruction.Operand is ITypeDefOrRef type ? SymbolicValue.FromType(type.FullName) : SymbolicValue.Unknown;
				state.Stack.Add(value); return true;
			}
			case Code.Ldelem_I: case Code.Ldelem_Ref: {
				var index = Pop(state).Integer; var array = Pop(state).Storage;
				state.Stack.Add(element_value?.Invoke(array, index) ?? SymbolicValue.Unknown); return true;
			}
			case Code.Ldftn: case Code.Ldvirtftn: {
				var receiver = code == Code.Ldvirtftn ? FlowMemory.ReadObject(state, Pop(state)) : null;
				if (receiver is not null && IsDefinitelyNull(receiver)) { state.PathTerminated = true; return true; }
				var target = instruction.Operand is IMethod reference ? Resolve(reference) : null;
				state.Stack.Add(target is null ? SymbolicValue.Unknown : SymbolicValue.FromMethod(target, code == Code.Ldvirtftn, receiver, CallSignature.For((IMethod)instruction.Operand)));
				return true;
			}
			case Code.Ldsfld: case Code.Ldsflda: {
				var field = (instruction.Operand as IField)?.ResolveFieldDef();
				if (field?.Constant?.Value is int constant) state.Stack.Add(SymbolicValue.FromInteger(Integers.Constant(constant)));
				else state.Stack.Add(code == Code.Ldsfld ? field_value(field) : field is null ? SymbolicValue.Unknown : SymbolicValue.FromStorage(field));
				return true;
			}
			case Code.Calli: {
				if (instruction.Operand is not MethodSig signature) return false;
				var pointer = Pop(state); if (Invocations is not null) pointer = Invocations.ResolvePointer(pointer, state);
				var values = new SymbolicValue[CallSignature.ArgumentCount(signature)];
				for (int index = values.Length - 1; index >= 0; index--) values[index] = Pop(state);
				var result = EvaluateIndirect(pointer, signature, values, state, instruction);
				if (CallSignature.ReturnsValue(signature)) state.Stack.Add(result);
				return true;
			}
			case Code.Call: case Code.Callvirt: case Code.Newobj: {
				if (instruction.Operand is not IMethod reference || reference.MethodSig is null) return false;
				var signature = CallSignature.For(reference);
				bool constructor = code == Code.Newobj;
				var values = new SymbolicValue[CallSignature.ArgumentCount(signature, constructor)];
				for (int index = values.Length - 1; index >= 0; index--) values[index] = Pop(state);
				if (code == Code.Callvirt && signature.HasThis && values.Length != 0 && IsDefinitelyNull(values[0])) { state.PathTerminated = true; return true; }
				var target = Resolve(reference);
				if (code == Code.Callvirt && values.Length != 0) target = Invocations?.DispatchReference(reference, values[0], state);
				object identity = new FlowAddress("allocation", state.Frame, instruction);
				var result = SymbolicValue.Unknown;
				if (Invocations?.TryEvaluate(reference, values, state, identity, constructor, out result) != true) {
					if (constructor) {
						result = SymbolicValue.FromObject(CallGraphIndex.SafeTypeName(reference.DeclaringType), identity);
						if (target is not null) Evaluate(target, new[] { result }.Concat(values).ToArray(), state, instruction);
					}
					else if (target is not null) result = Evaluate(target, values, state, instruction);
					else { InvalidateUnknownCall(state, values); result = SymbolicValue.UnknownBecause("method reference or runtime receiver could not be resolved: " + reference.FullName); }
				}
				if (constructor || CallSignature.ReturnsValue(signature)) {
					if (result.Integer is null && result.Methods.Count == 0 && IsInteger(signature.RetType)) result = OpaqueInteger();
					state.Stack.Add(result);
				}
				return true;
			}
			case Code.Neg: case Code.Not: {
				var operand = Pop(state); state.Stack.Add(code == Code.Neg && operand.Floating.HasValue ? SymbolicValue.FromFloating(-operand.Floating.Value) : SymbolicValue.FromInteger(Integers.Unary(code, operand.Integer))); return true;
			}
			case Code.Conv_I: case Code.Conv_U: case Code.Conv_I1: case Code.Conv_U1: case Code.Conv_I2: case Code.Conv_U2: case Code.Conv_I4: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U8: {
				var value = Pop(state);
				if (code is Code.Conv_I or Code.Conv_U && FlowMemory.IsAddress(value)) { state.Stack.Add(value); return true; }
				if (value.Methods.Count != 0) {
					if (code is Code.Conv_I1 or Code.Conv_U1 or Code.Conv_I2 or Code.Conv_U2 || code is Code.Conv_I4 or Code.Conv_U4 && !method.Module.Is32BitRequired) value = SymbolicValue.UnknownBecause("function pointer was narrowed to an unsafe width");
				}
				else value = SymbolicValue.FromInteger(Integers.Convert(code, value.Integer, method.Module.GetPointerSize(0)));
				state.Stack.Add(value); return true;
			}
			case Code.Add: case Code.Sub: case Code.Mul: case Code.Xor: case Code.And: case Code.Or:
			case Code.Shl: case Code.Shr: case Code.Shr_Un: case Code.Div: case Code.Div_Un: case Code.Rem: case Code.Rem_Un:
			case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un: {
				var right = Pop(state); var left = Pop(state);
				state.Stack.Add(Binary(code, left, right)); return true;
			}
			default: return false;
			}
		}
		internal static void InvalidateUnknownCall(SymbolicState state, IReadOnlyList<SymbolicValue> arguments) {
			state.ConservativeContext = true;
			var unknown = SymbolicValue.UnknownBecause("value may have been changed by an unresolved call");
			var reachable = new HashSet<object>(state.EscapedStorage);
			var pending = new Queue<object>();
			var children = state.Memory.Where(pair => pair.Key is FlowAddress).ToLookup(pair => ((FlowAddress)pair.Key).Owner);
			void Visit(SymbolicValue value) {
				foreach (var address in value.PossibleStorage) if (reachable.Add(address)) pending.Enqueue(address);
				foreach (var member in value.Members.Values) Visit(member);
			}
			foreach (var address in state.EscapedStorage) pending.Enqueue(address);
			foreach (var pair in state.Memory.Where(pair => pair.Key is FieldDef)) Visit(pair.Value);
			foreach (var value in arguments) Visit(value);
			while (pending.Count != 0) {
				var owner = pending.Dequeue();
				Visit(state.Load(owner));
				foreach (var pair in children[owner]) Visit(pair.Value);
			}
			state.EscapedStorage = state.EscapedStorage.Union(reachable);
			foreach (var key in state.Memory.Keys.Where(key => key is FieldDef || key is FlowAddress slot && slot.Kind is not "local" and not "argument" and not "length" && reachable.Contains(slot.Owner)).ToArray()) state.Store(key, SymbolicValue.Merge(new[] { state.Load(key), unknown }));
			foreach (var value in arguments) if (value.Storage is FlowAddress slot && slot.Kind is "local" or "argument" or "aggregate member") state.Store(slot, SymbolicValue.Merge(new[] { state.Load(slot), unknown }));
			foreach (var value in arguments) if (FlowMemory.AddressRoot(value.Storage) is FieldDef field) state.Store(field, SymbolicValue.Merge(new[] { state.Load(field), unknown }));
			foreach (var value in arguments.Where(value => value.TypeNames.Count == 0 && !value.IsBoxed)) foreach (var address in value.PossibleStorage) if (address is FlowAddress or FieldDef) state.Store(address, SymbolicValue.Merge(new[] { state.Load(address), unknown }));
		}
		internal static bool IsDefinitelyNull(SymbolicValue value) => !value.IsBoxed && !value.MayHaveUnknownTargets && value.Integer?.IsZero == true;
		internal static void Unsupported(Instruction instruction, SymbolicState state) {
			instruction.CalculateStackUsage(out int pushes, out int pops);
			if (instruction.OpCode.Code is Code.Cpblk or Code.Initblk or Code.Stind_R4 or Code.Stind_R8) InvalidateUnknownCall(state, state.Stack.ToArray());
			if (pops < 0) state.Stack.Clear(); else for (int index = 0; index < pops; index++) Pop(state);
			for (int index = 0; index < pushes; index++) state.Stack.Add(SymbolicValue.UnknownBecause("unsupported IL operation: " + instruction.OpCode.Name));
		}
		internal SymbolicValue EvaluateIndirect(SymbolicValue pointer, MethodSig signature, IReadOnlyList<SymbolicValue> arguments, SymbolicState state, object call_site) {
			var targets = pointer.Methods.Where(target => CallSignature.Compatible(pointer, target, signature)).ToArray();
			var results = new List<SymbolicValue>(); SymbolicState? normal = null, exceptional = null;
			foreach (var target in targets) {
				var branch = state.Clone(); var result = Evaluate(target, arguments, branch, call_site);
				if (branch.PathTerminated) { if (exceptional is null) exceptional = branch; else exceptional.MergeFrom(branch); }
				else { results.Add(result); if (normal is null) normal = branch; else normal.MergeFrom(branch); }
			}
			if (targets.Length == 0 || pointer.MayHaveUnknownTargets || targets.Length != pointer.Methods.Count) {
				LastFailure = "function pointer origin or signature is unresolved";
				results.Add(SymbolicValue.UnknownBecause(LastFailure));
				var branch = state.Clone(); InvalidateUnknownCall(branch, arguments);
				if (normal is null) normal = branch; else normal.MergeFrom(branch);
			}
			if ((normal ?? exceptional) is SymbolicState exit) state.ImportMemory(exit);
			state.PathTerminated = normal is null && exceptional is not null;
			return SymbolicValue.Merge(results);
		}

		public SymbolicValue Binary(Code code, SymbolicValue left, SymbolicValue right) {
			if (!left.IsBoxed && !right.IsBoxed && left.Floating.HasValue && right.Floating.HasValue) {
				if (FloatingConstant.Compare(code, left.Floating.Value, right.Floating.Value) is bool comparison) return SymbolicValue.FromInteger(Integers.Constant(comparison ? 1 : 0));
				return FloatingConstant.Binary(code, left.Floating.Value, right.Floating.Value) is double floating ? SymbolicValue.FromFloating(floating) : SymbolicValue.UnknownBecause("floating operation is not exactly representable in the supported precision model");
			}
			if (code is Code.Add or Code.Sub && FlowMemory.IsAddress(left) && right.Integer?.IsConstant == true) return FlowMemory.Offset(left, right.Integer.Value, code == Code.Sub);
			if (code == Code.Add && FlowMemory.IsAddress(right) && left.Integer?.IsConstant == true) return FlowMemory.Offset(right, left.Integer.Value, false);
			if (code is Code.Ceq or Code.Cgt_Un) {
				bool left_nonnull = !left.IsUnknown && !left.IsAmbiguous && !left.MayBeNull && (left.Storage is not null || left.IsBoxed || left.Methods.Count != 0 || left.Strings.Count != 0);
				bool right_null = right.Integer?.IsZero == true && !right.IsBoxed;
				if (left_nonnull && right_null) return SymbolicValue.FromInteger(Integers.Constant(code == Code.Ceq ? 0 : 1));
				if (code == Code.Ceq && (left.Storage is not null && Equals(left.Storage, right.Storage) || left.OpaqueIdentity is not null && Equals(left.OpaqueIdentity, right.OpaqueIdentity))) return SymbolicValue.FromInteger(Integers.Constant(1));
			}
			var a = Expression(left); var b = Expression(right);
			bool pointer = a is not null && pointer_expressions.Contains(a) || b is not null && pointer_expressions.Contains(b);
			if (pointer && code != Code.Xor && code != Code.Add && code != Code.Sub) return SymbolicValue.Unknown;
			var expression = Integers.Binary(code, a, b);
			if (pointer && expression is not null) pointer_expressions.Add(expression);
			return expression is not null && pointer_roots.TryGetValue(expression, out var target) ? target : SymbolicValue.FromInteger(expression);
		}
		IntegerExpression? Expression(SymbolicValue value) {
			if (value.IsBoxed) return null;
			if (value.Methods.Count != 0 && value.MethodKind != MethodValueKind.Pointer) return null;
			if (value.Methods.Count != 1) return value.Integer;
			var method = value.Methods.Single();
			var expression = Integers.Variable(Identity((method, value.VirtualMethods.Contains(method))));
			pointer_roots[expression] = value; pointer_expressions.Add(expression); return expression;
		}
		static bool IsInteger(TypeSig type) => type.ElementType == ElementType.I4 || type.ElementType == ElementType.U4;
		static SymbolicValue Pop(SymbolicState state) {
			if (state.Stack.Count == 0) return SymbolicValue.Unknown;
			int index = state.Stack.Count - 1; var value = state.Stack[index]; state.Stack.RemoveAt(index); return value;
		}
		internal static bool? Branch(Code code, SymbolicState state) {
			var right_value = Pop(state);
			var right = right_value.IsBoxed ? null : right_value.Integer;
			if (code == Code.Brtrue || code == Code.Brtrue_S || code == Code.Brfalse || code == Code.Brfalse_S) {
				bool is_false = code == Code.Brfalse || code == Code.Brfalse_S;
				if (right?.IsConstant == true) return right.IsZero == is_false;
				if (!right_value.IsUnknown && !right_value.IsAmbiguous && !right_value.MayBeNull && (right_value.IsBoxed || right_value.Storage is not null || right_value.Strings.Count != 0 || right_value.Methods.Count != 0)) return !is_false;
				return null;
			}
			var left_value = Pop(state);
			if (!left_value.IsBoxed && !right_value.IsBoxed && left_value.Floating.HasValue && right_value.Floating.HasValue) return FloatingConstant.Compare(code, left_value.Floating.Value, right_value.Floating.Value);
			if (left_value.OpaqueIdentity is not null && Equals(left_value.OpaqueIdentity, right_value.OpaqueIdentity) || left_value.Storage is not null && Equals(left_value.Storage, right_value.Storage)) {
				if (code is Code.Beq or Code.Beq_S) return true;
				if (code is Code.Bne_Un or Code.Bne_Un_S) return false;
			}
			var left = left_value.Integer;
			if (left?.IsConstant != true || right?.IsConstant != true) return null;
			long a = left.Value, b = right.Value;
			if ((left.BitWidth == 0 || right.BitWidth == 0) && (a < 0 || b < 0) && code is Code.Bge_Un or Code.Bge_Un_S or Code.Bgt_Un or Code.Bgt_Un_S or Code.Ble_Un or Code.Ble_Un_S or Code.Blt_Un or Code.Blt_Un_S) return null;
			bool wide = left.BitWidth == 64 || right.BitWidth == 64;
			return code switch {
				Code.Beq or Code.Beq_S => a == b, Code.Bne_Un or Code.Bne_Un_S => a != b,
				Code.Bge or Code.Bge_S => a >= b, Code.Bgt or Code.Bgt_S => a > b,
				Code.Ble or Code.Ble_S => a <= b, Code.Blt or Code.Blt_S => a < b,
				Code.Bge_Un or Code.Bge_Un_S => wide ? (ulong)a >= (ulong)b : (uint)a >= (uint)b,
				Code.Bgt_Un or Code.Bgt_Un_S => wide ? (ulong)a > (ulong)b : (uint)a > (uint)b,
				Code.Ble_Un or Code.Ble_Un_S => wide ? (ulong)a <= (ulong)b : (uint)a <= (uint)b,
				Code.Blt_Un or Code.Blt_Un_S => wide ? (ulong)a < (ulong)b : (uint)a < (uint)b,
				_ => null,
			};
		}
		static int LocalIndex(Instruction instruction) => instruction.OpCode.Code switch {
			Code.Ldloc_0 or Code.Stloc_0 => 0, Code.Ldloc_1 or Code.Stloc_1 => 1,
			Code.Ldloc_2 or Code.Stloc_2 => 2, Code.Ldloc_3 or Code.Stloc_3 => 3,
			Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S => instruction.Operand is Local local ? local.Index : -1, _ => -1,
		};
		static int ArgumentIndex(Instruction instruction) => instruction.OpCode.Code switch {
			Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
			Code.Ldarg or Code.Ldarg_S => instruction.Operand is Parameter parameter ? parameter.Index : -1, _ => -1,
		};
	}
}
