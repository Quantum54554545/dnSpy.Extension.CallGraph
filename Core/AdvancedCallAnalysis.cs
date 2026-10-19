using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class ReflectionLookup : IEquatable<ReflectionLookup> {
		public string TypeName { get; }
		public string MethodName { get; }

		public ReflectionLookup(string type_name, string method_name) {
			TypeName = type_name;
			MethodName = method_name;
		}

		public bool Equals(ReflectionLookup? other) => other is not null &&
			StringComparer.Ordinal.Equals(TypeName, other.TypeName) &&
			StringComparer.Ordinal.Equals(MethodName, other.MethodName);
		public override bool Equals(object? obj) => Equals(obj as ReflectionLookup);
		public override int GetHashCode() => HashCode.Combine(
			StringComparer.Ordinal.GetHashCode(TypeName), StringComparer.Ordinal.GetHashCode(MethodName));
	}

	sealed class SymbolicValue {
		public static readonly SymbolicValue Unknown = new SymbolicValue(true);

		public bool IsUnknown { get; }
		public HashSet<string> TypeNames { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<string> Strings { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<MethodDef> Methods { get; } = new HashSet<MethodDef>();
		public HashSet<MethodDef> VirtualMethods { get; } = new HashSet<MethodDef>();
		public HashSet<ReflectionLookup> Lookups { get; } = new HashSet<ReflectionLookup>();

		SymbolicValue(bool is_unknown = false) => IsUnknown = is_unknown;

		public static SymbolicValue FromType(string type_name) {
			var result = new SymbolicValue();
			if (!string.IsNullOrEmpty(type_name))
				result.TypeNames.Add(type_name);
			return result;
		}

		public static SymbolicValue FromString(string value) {
			var result = new SymbolicValue();
			result.Strings.Add(value);
			return result;
		}

		public static SymbolicValue FromMethod(MethodDef method, bool is_virtual = false) {
			var result = new SymbolicValue();
			result.Methods.Add(method);
			if (is_virtual)
				result.VirtualMethods.Add(method);
			return result;
		}

		public static SymbolicValue FromLookup(IEnumerable<string> type_names, IEnumerable<string> method_names) {
			var result = new SymbolicValue();
			foreach (string type_name in type_names)
			foreach (string method_name in method_names)
				result.Lookups.Add(new ReflectionLookup(type_name, method_name));
			return result;
		}

		public SymbolicValue Clone() {
			if (IsUnknown)
				return Unknown;
			var result = new SymbolicValue();
			result.TypeNames.UnionWith(TypeNames);
			result.Strings.UnionWith(Strings);
			result.Methods.UnionWith(Methods);
			result.VirtualMethods.UnionWith(VirtualMethods);
			result.Lookups.UnionWith(Lookups);
			return result;
		}

		public static SymbolicValue Merge(IEnumerable<SymbolicValue> values) {
			var items = values.ToArray();
			if (items.Length == 0 || items.Any(value => value.IsUnknown))
				return Unknown;
			var result = new SymbolicValue();
			foreach (var value in items) {
				result.TypeNames.UnionWith(value.TypeNames);
				result.Strings.UnionWith(value.Strings);
				result.Methods.UnionWith(value.Methods);
				result.VirtualMethods.UnionWith(value.VirtualMethods);
				result.Lookups.UnionWith(value.Lookups);
			}
			if (result.TypeNames.Count > 64 || result.Strings.Count > 64 || result.Methods.Count > 64 || result.Lookups.Count > 64)
				return Unknown;
			return result;
		}

		public bool SameAs(SymbolicValue other) => IsUnknown == other.IsUnknown &&
			TypeNames.SetEquals(other.TypeNames) && Strings.SetEquals(other.Strings) &&
			Methods.SetEquals(other.Methods) && VirtualMethods.SetEquals(other.VirtualMethods) &&
			Lookups.SetEquals(other.Lookups);
	}

	sealed class SymbolicState {
		public List<SymbolicValue> Stack { get; }
		public Dictionary<int, SymbolicValue> Locals { get; }

		public SymbolicState() {
			Stack = new List<SymbolicValue>();
			Locals = new Dictionary<int, SymbolicValue>();
		}

		SymbolicState(List<SymbolicValue> stack, Dictionary<int, SymbolicValue> locals) {
			Stack = stack;
			Locals = locals;
		}

		public SymbolicState Clone() => new SymbolicState(Stack.Select(value => value.Clone()).ToList(),
			Locals.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()));

		public bool MergeFrom(SymbolicState other) {
			bool changed = false;
			if (Stack.Count != other.Stack.Count) {
				int count = Math.Max(Stack.Count, other.Stack.Count);
				if (Stack.Count != count || Stack.Any(value => !value.IsUnknown)) {
					Stack.Clear();
					for (int i = 0; i < count; i++)
						Stack.Add(SymbolicValue.Unknown);
					changed = true;
				}
			}
			else {
				for (int i = 0; i < Stack.Count; i++) {
					var merged = SymbolicValue.Merge(new[] { Stack[i], other.Stack[i] });
					if (Stack[i].SameAs(merged))
						continue;
					Stack[i] = merged;
					changed = true;
				}
			}
			foreach (int index in Locals.Keys.Union(other.Locals.Keys).ToArray()) {
				var left = Locals.TryGetValue(index, out var left_value) ? left_value : SymbolicValue.Unknown;
				var right = other.Locals.TryGetValue(index, out var right_value) ? right_value : SymbolicValue.Unknown;
				var merged = SymbolicValue.Merge(new[] { left, right });
				if (left.SameAs(merged) && Locals.ContainsKey(index))
					continue;
				Locals[index] = merged;
				changed = true;
			}
			return changed;
		}
	}

	static class AdvancedCallAnalysis {
		public static void Scan(MethodDef method, Dictionary<IMethod, MethodDef?> resolved_methods,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites,
			List<VirtualCallSite> virtual_sites, List<ReflectionTarget> reflection_sites,
			CancellationToken cancellation_token) {
			var instructions = method.Body.Instructions.ToArray();
			if (instructions.Length == 0 || !instructions.Any(HasDynamicSeed))
				return;
			var indexes = instructions.Select((instruction, index) => (instruction, index))
				.ToDictionary(pair => pair.instruction, pair => pair.index);
			var states = new Dictionary<Instruction, SymbolicState>();
			var queue = new Queue<Instruction>();
			Propagate(instructions[0], new SymbolicState(), states, queue);
			foreach (var handler in method.Body.ExceptionHandlers) {
				if (handler.FilterStart is not null)
					Propagate(handler.FilterStart, ExceptionState(true), states, queue);
				if (handler.HandlerStart is not null)
					Propagate(handler.HandlerStart, ExceptionState(handler.HandlerType == ExceptionHandlerType.Catch ||
						handler.HandlerType == ExceptionHandlerType.Filter), states, queue);
			}
			while (queue.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var instruction = queue.Dequeue();
				var state = states[instruction].Clone();
				Simulate(method, instruction, state, resolved_methods, call_sites, virtual_sites, reflection_sites);
				foreach (var successor in Successors(instruction, instructions, indexes)) {
					var outgoing_state = state;
					if (instruction.OpCode.Code == Code.Leave || instruction.OpCode.Code == Code.Leave_S) {
						outgoing_state = state.Clone();
						outgoing_state.Stack.Clear();
					}
					Propagate(successor, outgoing_state, states, queue);
				}
			}
		}

		static bool HasDynamicSeed(Instruction instruction) {
			var code = instruction.OpCode.Code;
			if (code == Code.Calli || code == Code.Ldftn || code == Code.Ldvirtftn || code == Code.Ldtoken)
				return true;
			if (instruction.Operand is not IMethod method)
				return false;
			string declaring_type = CallGraphIndex.SafeTypeName(method.DeclaringType);
			string name = method.Name?.ToString() ?? string.Empty;
			return declaring_type == "System.Type" && name == "GetMethod" ||
				declaring_type == "System.Delegate" && name == "CreateDelegate" ||
				(declaring_type == "System.Reflection.MethodBase" || declaring_type == "System.Reflection.MethodInfo") &&
				(name == "GetMethodFromHandle" || name == "Invoke" || name == "get_MethodHandle");
		}

		static SymbolicState ExceptionState(bool has_exception) {
			var state = new SymbolicState();
			if (has_exception)
				state.Stack.Add(SymbolicValue.Unknown);
			return state;
		}

		static void Propagate(Instruction instruction, SymbolicState state,
			Dictionary<Instruction, SymbolicState> states, Queue<Instruction> queue) {
			if (!states.TryGetValue(instruction, out var existing)) {
				states.Add(instruction, state.Clone());
				queue.Enqueue(instruction);
			}
			else if (existing.MergeFrom(state))
				queue.Enqueue(instruction);
		}

		static IEnumerable<Instruction> Successors(Instruction instruction, Instruction[] instructions,
			Dictionary<Instruction, int> indexes) {
			int index = indexes[instruction];
			Instruction? next = index + 1 < instructions.Length ? instructions[index + 1] : null;
			var code = instruction.OpCode.Code;
			if (code == Code.Switch) {
				if (instruction.Operand is IList<Instruction> targets)
					foreach (var target in targets)
						yield return target;
				if (next is not null)
					yield return next;
				yield break;
			}
			if (IsUnconditionalBranch(code)) {
				if (instruction.Operand is Instruction target)
					yield return target;
				yield break;
			}
			if (IsConditionalBranch(code)) {
				if (instruction.Operand is Instruction target)
					yield return target;
				if (next is not null)
					yield return next;
				yield break;
			}
			if (!IsTerminal(code) && next is not null)
				yield return next;
		}

		static bool IsUnconditionalBranch(Code code) =>
			code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S;

		static bool IsConditionalBranch(Code code) => code == Code.Brfalse || code == Code.Brfalse_S ||
			code == Code.Brtrue || code == Code.Brtrue_S || code == Code.Beq || code == Code.Beq_S ||
			code == Code.Bge || code == Code.Bge_S || code == Code.Bge_Un || code == Code.Bge_Un_S ||
			code == Code.Bgt || code == Code.Bgt_S || code == Code.Bgt_Un || code == Code.Bgt_Un_S ||
			code == Code.Ble || code == Code.Ble_S || code == Code.Ble_Un || code == Code.Ble_Un_S ||
			code == Code.Blt || code == Code.Blt_S || code == Code.Blt_Un || code == Code.Blt_Un_S ||
			code == Code.Bne_Un || code == Code.Bne_Un_S;

		static bool IsTerminal(Code code) => code == Code.Ret || code == Code.Throw || code == Code.Rethrow ||
			code == Code.Endfinally || code == Code.Endfilter || code == Code.Jmp;

		static void Simulate(MethodDef source, Instruction instruction, SymbolicState state,
			Dictionary<IMethod, MethodDef?> resolved_methods,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites,
			List<VirtualCallSite> virtual_sites, List<ReflectionTarget> reflection_sites) {
			var code = instruction.OpCode.Code;
			if (TryLoadLocal(code, instruction, state, out var loaded_local)) {
				state.Stack.Add(loaded_local);
				return;
			}
			if (TryStoreLocal(code, instruction, state))
				return;
			switch (code) {
			case Code.Ldstr:
				state.Stack.Add(SymbolicValue.FromString(instruction.Operand as string ?? string.Empty));
				return;
			case Code.Ldtoken:
				if (instruction.Operand is IMethod method_reference) {
					var method = CallGraphIndex.Resolve(method_reference, resolved_methods);
					state.Stack.Add(method is null ? SymbolicValue.Unknown : SymbolicValue.FromMethod(method));
				}
				else if (instruction.Operand is ITypeDefOrRef type_reference)
					state.Stack.Add(SymbolicValue.FromType(CallGraphIndex.SafeTypeName(type_reference)));
				else
					state.Stack.Add(SymbolicValue.Unknown);
				return;
			case Code.Ldftn:
			case Code.Ldvirtftn:
				if (code == Code.Ldvirtftn)
					Pop(state.Stack);
				if (instruction.Operand is IMethod function_reference) {
					var function = CallGraphIndex.Resolve(function_reference, resolved_methods);
					state.Stack.Add(function is null ? SymbolicValue.Unknown :
						SymbolicValue.FromMethod(function, code == Code.Ldvirtftn));
				}
				else
					state.Stack.Add(SymbolicValue.Unknown);
				return;
			case Code.Dup:
				state.Stack.Add(state.Stack.Count == 0 ? SymbolicValue.Unknown : state.Stack[state.Stack.Count - 1].Clone());
				return;
			case Code.Pop:
				Pop(state.Stack);
				return;
			case Code.Ldarg_0:
				state.Stack.Add(!source.IsStatic && source.DeclaringType is not null
					? SymbolicValue.FromType(CallGraphIndex.SafeTypeName(source.DeclaringType)) : SymbolicValue.Unknown);
				return;
			case Code.Ldarg:
			case Code.Ldarg_S:
			case Code.Ldarg_1:
			case Code.Ldarg_2:
			case Code.Ldarg_3:
				state.Stack.Add(SymbolicValue.Unknown);
				return;
			case Code.Castclass:
			case Code.Isinst:
			case Code.Conv_I:
			case Code.Conv_U:
				return;
			case Code.Calli:
				SimulateCalli(source, instruction, state, call_sites, virtual_sites);
				return;
			case Code.Call:
			case Code.Callvirt:
			case Code.Newobj:
				SimulateManagedCall(source, instruction, state, reflection_sites);
				return;
			}
			for (int i = 0; i < FixedPopCount(instruction.OpCode.StackBehaviourPop); i++)
				Pop(state.Stack);
			for (int i = 0; i < FixedPushCount(instruction.OpCode.StackBehaviourPush); i++)
				state.Stack.Add(SymbolicValue.Unknown);
		}

		static void SimulateCalli(MethodDef source, Instruction instruction, SymbolicState state,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites,
			List<VirtualCallSite> virtual_sites) {
			var signature = instruction.Operand as MethodSig;
			int argument_count = (signature?.Params.Count ?? 0) + (signature?.HasThis == true ? 1 : 0);
			var pointer = Pop(state.Stack);
			for (int i = 0; i < argument_count; i++)
				Pop(state.Stack);
			foreach (var target in pointer.Methods) {
				CallGraphIndex.AddCallSite(call_sites, source, target, instruction.Offset, "calli", false);
				if (pointer.VirtualMethods.Contains(target))
					virtual_sites.Add(new VirtualCallSite(source, target, instruction.Offset, "possible calli target"));
			}
			if (signature is not null && signature.RetType.ElementType != ElementType.Void)
				state.Stack.Add(SymbolicValue.Unknown);
		}

		static void SimulateManagedCall(MethodDef source, Instruction instruction, SymbolicState state,
			List<ReflectionTarget> reflection_sites) {
			if (instruction.Operand is not IMethod method_reference) {
				state.Stack.Add(SymbolicValue.Unknown);
				return;
			}
			var signature = method_reference.MethodSig;
			int argument_count = signature?.Params.Count ?? 0;
			var arguments = new SymbolicValue[argument_count];
			for (int i = argument_count - 1; i >= 0; i--)
				arguments[i] = Pop(state.Stack);
			bool is_constructor = instruction.OpCode.Code == Code.Newobj;
			var instance = !is_constructor && signature?.HasThis == true ? Pop(state.Stack) : SymbolicValue.Unknown;
			string declaring_type = CallGraphIndex.SafeTypeName(method_reference.DeclaringType);
			string name = method_reference.Name?.ToString() ?? string.Empty;
			SymbolicValue result;

			if (declaring_type == "System.Type" && name == "GetTypeFromHandle")
				result = arguments.Length == 0 ? SymbolicValue.Unknown : arguments[0];
			else if ((declaring_type == "System.Reflection.MethodBase" || declaring_type == "System.Reflection.MethodInfo") &&
				name == "get_MethodHandle")
				result = instance;
			else if (declaring_type == "System.Type" && name == "GetMethod") {
				var names = arguments.SelectMany(value => value.Strings).Distinct(StringComparer.Ordinal).ToArray();
				result = SymbolicValue.FromLookup(instance.TypeNames, names);
				AddReflectionTarget(reflection_sites, source, result, instruction.Offset, "reflection: Type.GetMethod");
			}
			else if ((declaring_type == "System.Reflection.MethodBase" || declaring_type == "System.Reflection.MethodInfo") &&
				name == "GetMethodFromHandle") {
				result = SymbolicValue.Merge(arguments);
				AddReflectionTarget(reflection_sites, source, result, instruction.Offset, "reflection: GetMethodFromHandle");
			}
			else if (declaring_type == "System.Delegate" && name == "CreateDelegate") {
				result = MergeKnown(arguments.Concat(new[] { instance }));
				if (result.Methods.Count == 0 && result.Lookups.Count == 0) {
					var type_names = arguments.SelectMany(value => value.TypeNames).Distinct(StringComparer.Ordinal);
					var method_names = arguments.SelectMany(value => value.Strings).Distinct(StringComparer.Ordinal);
					result = SymbolicValue.FromLookup(type_names, method_names);
				}
				AddReflectionTarget(reflection_sites, source, result, instruction.Offset, "reflection: Delegate.CreateDelegate");
			}
			else if ((declaring_type == "System.Reflection.MethodBase" || declaring_type == "System.Reflection.MethodInfo") &&
				name == "Invoke") {
				result = SymbolicValue.Unknown;
				AddReflectionTarget(reflection_sites, source, instance, instruction.Offset, "reflection: MethodInfo.Invoke");
			}
			else if ((declaring_type == "System.Delegate" && name == "DynamicInvoke") ||
				name == "Invoke" && instance.Methods.Count != 0) {
				result = SymbolicValue.Unknown;
				AddReflectionTarget(reflection_sites, source, instance, instruction.Offset, "delegate invocation");
			}
			else if (is_constructor) {
				result = SymbolicValue.FromType(CallGraphIndex.SafeTypeName(method_reference.DeclaringType));
				var function = arguments.FirstOrDefault(value => value.Methods.Count != 0);
				if (function is not null) {
					result = MergeKnown(new[] { result, function });
					AddReflectionTarget(reflection_sites, source, function, instruction.Offset, "delegate constructor");
				}
			}
			else
				result = SymbolicValue.Unknown;

			if (is_constructor || signature is not null && signature.RetType.ElementType != ElementType.Void)
				state.Stack.Add(result);
		}

		static SymbolicValue MergeKnown(IEnumerable<SymbolicValue> values) {
			var known = values.Where(value => !value.IsUnknown).ToArray();
			return known.Length == 0 ? SymbolicValue.Unknown : SymbolicValue.Merge(known);
		}

		static void AddReflectionTarget(List<ReflectionTarget> targets, MethodDef source,
			SymbolicValue value, uint offset, string operation) {
			var type_names = value.Lookups.Select(lookup => lookup.TypeName).Concat(value.TypeNames).ToArray();
			var method_names = value.Lookups.Select(lookup => lookup.MethodName).Concat(value.Strings).ToArray();
			if (value.Methods.Count == 0 && !value.Lookups.Any() && (type_names.Length == 0 || method_names.Length == 0))
				return;
			targets.Add(new ReflectionTarget(source, type_names, method_names, value.Methods, offset, operation));
		}

		static bool TryLoadLocal(Code code, Instruction instruction, SymbolicState state, out SymbolicValue value) {
			int index = code switch {
				Code.Ldloc_0 => 0,
				Code.Ldloc_1 => 1,
				Code.Ldloc_2 => 2,
				Code.Ldloc_3 => 3,
				Code.Ldloc or Code.Ldloc_S => instruction.Operand is Local local ? local.Index : -1,
				_ => -1,
			};
			if (index < 0) {
				value = SymbolicValue.Unknown;
				return false;
			}
			value = state.Locals.TryGetValue(index, out var local_value) ? local_value.Clone() : SymbolicValue.Unknown;
			return true;
		}

		static bool TryStoreLocal(Code code, Instruction instruction, SymbolicState state) {
			int index = code switch {
				Code.Stloc_0 => 0,
				Code.Stloc_1 => 1,
				Code.Stloc_2 => 2,
				Code.Stloc_3 => 3,
				Code.Stloc or Code.Stloc_S => instruction.Operand is Local local ? local.Index : -1,
				_ => -1,
			};
			if (index < 0)
				return false;
			state.Locals[index] = Pop(state.Stack);
			return true;
		}

		static SymbolicValue Pop(List<SymbolicValue> stack) {
			if (stack.Count == 0)
				return SymbolicValue.Unknown;
			int index = stack.Count - 1;
			var result = stack[index];
			stack.RemoveAt(index);
			return result;
		}

		static int FixedPopCount(StackBehaviour behaviour) => behaviour switch {
			StackBehaviour.Pop0 => 0,
			StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
			StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or
			StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
			StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
			StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or
			StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or
			StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
			_ => 0,
		};

		static int FixedPushCount(StackBehaviour behaviour) => behaviour switch {
			StackBehaviour.Push0 => 0,
			StackBehaviour.Push1_push1 => 2,
			StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or
			StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
			_ => 0,
		};
	}

	static class WorkspaceCallResolver {
		public static void AddExtendedEdges(HashSet<MethodDef> methods, IEnumerable<VirtualCallSite> virtual_sites,
			IEnumerable<ReflectionTarget> reflection_sites,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites) {
			var type_map = methods.Select(method => method.DeclaringType).Where(type => type is not null).Distinct()
				.GroupBy(CallGraphIndex.SafeTypeName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var signature_map = methods.GroupBy(MethodSignatureKey, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var explicit_overrides = BuildExplicitOverrideMap(methods);
			var method_name_map = methods.GroupBy(CallGraphIndex.SafeMethodName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var relation_cache = new Dictionary<(TypeDef Candidate, TypeDef Declaration, bool Interface), bool>();
			foreach (var site in virtual_sites.GroupBy(site => (site.Source, site.Declaration, site.Offset, site.Operation))
				.Select(group => group.First())) {
				foreach (var target in FindVirtualTargets(site.Declaration, signature_map, explicit_overrides,
					type_map, relation_cache))
					CallGraphIndex.AddCallSite(call_sites, site.Source, target, site.Offset, site.Operation, true);
			}
			foreach (var site in reflection_sites.GroupBy(site => (site.Source, site.Offset, site.Operation,
					Types: string.Join("\0", site.TypeNames), Names: string.Join("\0", site.MethodNames)))
				.Select(group => group.First())) {
				foreach (var target in ResolveReflectionTargets(site, methods, type_map, method_name_map))
					CallGraphIndex.AddCallSite(call_sites, site.Source, target, site.Offset, site.Operation, true);
			}
		}

		static Dictionary<string, MethodDef[]> BuildExplicitOverrideMap(IEnumerable<MethodDef> methods) {
			var result = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var method in methods) {
				foreach (var method_override in method.Overrides) {
					string key = MethodIdentity(method_override.MethodDeclaration);
					if (!result.TryGetValue(key, out var values))
						result.Add(key, values = new List<MethodDef>());
					values.Add(method);
				}
			}
			return result.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToArray(), StringComparer.Ordinal);
		}

		static IEnumerable<MethodDef> FindVirtualTargets(MethodDef declaration,
			Dictionary<string, MethodDef[]> signature_map, Dictionary<string, MethodDef[]> explicit_overrides,
			Dictionary<string, TypeDef[]> type_map,
			Dictionary<(TypeDef Candidate, TypeDef Declaration, bool Interface), bool> relation_cache) {
			var result = new HashSet<MethodDef>();
			if (explicit_overrides.TryGetValue(MethodIdentity(declaration), out var explicit_methods))
				result.UnionWith(explicit_methods.Where(IsRuntimeMethod));
			if (declaration.DeclaringType is not TypeDef declaring_type)
				return result;
			bool is_interface = declaring_type.IsInterface;
			if (signature_map.TryGetValue(MethodSignatureKey(declaration), out var candidates)) {
				foreach (var candidate in candidates) {
					if (ReferenceEquals(candidate, declaration) || !IsRuntimeMethod(candidate) ||
						candidate.DeclaringType is not TypeDef candidate_type)
						continue;
					var key = (candidate_type, declaring_type, is_interface);
					if (!relation_cache.TryGetValue(key, out bool related)) {
						related = is_interface ? ImplementsInterface(candidate_type, declaring_type, type_map) :
							DerivesFrom(candidate_type, declaring_type, type_map);
						relation_cache[key] = related;
					}
					if (related)
						result.Add(candidate);
				}
			}
			return result;
		}

		static IEnumerable<MethodDef> ResolveReflectionTargets(ReflectionTarget site, HashSet<MethodDef> methods,
			Dictionary<string, TypeDef[]> type_map, Dictionary<string, MethodDef[]> method_name_map) {
			var result = new HashSet<MethodDef>(site.ExactMethods.Where(methods.Contains));
			if (site.TypeNames.Count == 0 || site.MethodNames.Count == 0)
				return result;
			foreach (string type_name in site.TypeNames) {
				if (!type_map.TryGetValue(type_name, out var requested_types))
					continue;
				foreach (string method_name in site.MethodNames) {
					if (!method_name_map.TryGetValue(method_name, out var candidates))
						continue;
					foreach (var candidate in candidates) {
						if (candidate.DeclaringType is not TypeDef candidate_type)
							continue;
						if (requested_types.Any(requested => ReferenceEquals(candidate_type, requested) ||
							DerivesFrom(requested, candidate_type, type_map)))
							result.Add(candidate);
					}
				}
			}
			return result;
		}

		static bool ImplementsInterface(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>();
			queue.Enqueue(candidate);
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				foreach (var implementation in current.Interfaces) {
					foreach (var resolved in ResolveTypes(implementation.Interface, type_map)) {
						if (SameType(resolved, target))
							return true;
						queue.Enqueue(resolved);
					}
				}
				foreach (var base_type in ResolveTypes(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static bool DerivesFrom(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>(ResolveTypes(candidate.BaseType, type_map));
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				if (SameType(current, target))
					return true;
				foreach (var base_type in ResolveTypes(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static IEnumerable<TypeDef> ResolveTypes(ITypeDefOrRef? reference, Dictionary<string, TypeDef[]> type_map) {
			if (reference is null)
				yield break;
			if (reference is TypeDef definition) {
				yield return definition;
				yield break;
			}
			TypeDef? resolved;
			try { resolved = reference.ResolveTypeDef(); }
			catch (Exception) { resolved = null; }
			if (resolved is not null) {
				yield return resolved;
				yield break;
			}
			if (type_map.TryGetValue(CallGraphIndex.SafeTypeName(reference), out var candidates))
				foreach (var candidate in candidates)
					yield return candidate;
		}

		static bool SameType(TypeDef left, TypeDef right) => ReferenceEquals(left, right) ||
			StringComparer.Ordinal.Equals(CallGraphIndex.SafeTypeName(left), CallGraphIndex.SafeTypeName(right));

		static bool IsRuntimeMethod(MethodDef method) {
			try { return !method.IsStatic && !method.IsAbstract && method.HasBody; }
			catch (Exception) { return false; }
		}

		static string MethodSignatureKey(MethodDef method) {
			try {
				return CallGraphIndex.SafeMethodName(method) + "`" + method.GenericParameters.Count + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return CallGraphIndex.SafeMethodName(method); }
		}

		static string MethodIdentity(IMethod method) {
			try {
				return CallGraphIndex.SafeTypeName(method.DeclaringType) + "::" +
					(method.Name?.ToString() ?? string.Empty) + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return method.FullName ?? string.Empty; }
		}
	}
}
