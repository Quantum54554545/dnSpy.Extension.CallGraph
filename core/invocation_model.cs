using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record invocation_target(MethodDef method, symbolic_value[] arguments, string operation, bool potential);

	sealed class invocation_model {
		readonly Dictionary<string, TypeDef[]> types;
		readonly Func<IMethod, MethodDef?> resolve;
		readonly Func<MethodDef, IReadOnlyList<symbolic_value>, symbolic_state, symbolic_value> evaluate;
		readonly Dictionary<(IMethod, string), MethodDef?> dispatch_cache = new Dictionary<(IMethod, string), MethodDef?>();
		readonly Dictionary<string, ITypeDefOrRef> constructed_types = new Dictionary<string, ITypeDefOrRef>(StringComparer.Ordinal);
		readonly HashSet<string> ambiguous_types = new HashSet<string>(StringComparer.Ordinal);
		readonly collection_flow_model collections = new collection_flow_model();
		readonly Dictionary<Instruction, ITypeDefOrRef> constrained_calls = new Dictionary<Instruction, ITypeDefOrRef>();
		readonly HashSet<MethodDef> scanned_prefixes = new HashSet<MethodDef>();
		readonly integer_expression.factory constants = new integer_expression.factory();
		public invocation_model(IEnumerable<ModuleDef> modules, Func<IMethod, MethodDef?> resolve,
			Func<MethodDef, IReadOnlyList<symbolic_value>, symbolic_state, symbolic_value> evaluate, Dictionary<string, TypeDef[]>? shared_types = null) {
			this.types = shared_types ?? modules.SelectMany(module => module.GetTypes()).GroupBy(type => type.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			this.resolve = resolve; this.evaluate = evaluate;
		}
		public TypeDef? type(string name) => this.constructed_types.TryGetValue(name, out var reference) ? this.resolve_type(reference) :
			this.types.TryGetValue(name, out var values) && values.Length == 1 ? values[0] : null;
		public symbolic_value new_object(IMethod? reference, object identity) {
			string name = call_graph_index.safe_type_name(reference?.DeclaringType);
			if (reference?.DeclaringType is TypeSpec type && !this.ambiguous_types.Contains(name)) {
				if (this.constructed_types.TryGetValue(name, out var previous) && !StringComparer.OrdinalIgnoreCase.Equals(previous.DefinitionAssembly?.FullName, type.DefinitionAssembly?.FullName)) {
					this.constructed_types.Remove(name); this.ambiguous_types.Add(name);
				}
				else this.constructed_types[name] = type;
			}
			return symbolic_value.from_object(name, identity);
		}
		public bool is_framework_model(IMethod reference) {
			string type = call_graph_index.safe_type_name(reference.DeclaringType);
			return framework_identity.is_framework_type(reference.DeclaringType) && (type.StartsWith("System.", StringComparison.Ordinal) || type.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal));
		}
		public TypeDef? resolve_type(ITypeDefOrRef? reference) {
			if (reference is TypeSpec specification && specification.TypeSig is GenericInstSig instance) reference = instance.GenericType.TypeDefOrRef;
			if (reference is TypeDef definition) return definition;
			try { if (reference?.ResolveTypeDef() is TypeDef resolved) return resolved; } catch (Exception) { }
			var candidate = reference is not null && this.types.TryGetValue(reference.FullName, out var candidates) && candidates.Length == 1 ? candidates[0] : null;
			return candidate is not null && (reference?.DefinitionAssembly is null || StringComparer.OrdinalIgnoreCase.Equals(candidate.DefinitionAssembly?.FullName, reference.DefinitionAssembly.FullName)) ? candidate : null;
		}
		public bool is_delegate(IMethod reference) {
			return this.is_delegate_type(reference.DeclaringType);
		}
		public bool is_delegate_type(ITypeDefOrRef? reference) {
			string name = call_graph_index.safe_type_name(reference);
			if (framework_identity.is_framework_type(reference) && name is "System.Threading.ThreadStart" or "System.Threading.ParameterizedThreadStart" or "System.Threading.WaitCallback" or "System.Threading.WaitOrTimerCallback" or "System.Threading.TimerCallback") return true;
			if (framework_identity.is_framework_type(reference) && (name == "System.Action" || name.StartsWith("System.Action`", StringComparison.Ordinal) || name.StartsWith("System.Func`", StringComparison.Ordinal) || name.StartsWith("System.Predicate`", StringComparison.Ordinal) || name == "System.EventHandler" || name.StartsWith("System.EventHandler`", StringComparison.Ordinal) || name.StartsWith("System.Comparison`", StringComparison.Ordinal))) return true;
			var type = this.resolve_type(reference);
			return call_graph_index.safe_type_name(type?.BaseType) is "System.MulticastDelegate" or "System.Delegate";
		}
		public MethodDef? dispatch(MethodDef declaration, symbolic_value receiver, symbolic_state state, IMethod? reference = null) {
			receiver = flow_memory.read_object(state, receiver);
			if (!receiver.is_exact_type || receiver.type_names.Count != 1 || this.type(receiver.type_names.Single()) is not TypeDef type) return null;
			string name = receiver.type_names.Single(); var key = (reference ?? declaration, name: name);
			if (this.dispatch_cache.TryGetValue(key, out var cached)) return cached;
			this.constructed_types.TryGetValue(name, out var instance);
			return this.dispatch_cache[key] = virtual_dispatch_resolver.resolve(declaration, type, this.resolve_type, reference, instance);
		}
		public symbolic_value constrained_receiver(MethodDef owner, Instruction call, symbolic_value receiver, symbolic_state state) {
			if (this.scanned_prefixes.Add(owner)) {
				ITypeDefOrRef? constraint = null;
				foreach (var instruction in owner.Body.Instructions) {
					if (instruction.OpCode.Code == Code.Constrained) { constraint = instruction.Operand as ITypeDefOrRef; continue; }
					if (instruction.OpCode.Code is Code.Tailcall or Code.Readonly or Code.Volatile or Code.Unaligned) continue;
					if (instruction.OpCode.Code == Code.Callvirt && constraint is not null) this.constrained_calls[instruction] = constraint;
					constraint = null;
				}
			}
			if (!this.constrained_calls.TryGetValue(call, out var reference)) return receiver;
			var value = flow_memory.read_object(state, receiver);
			if (this.resolve_type(reference) is not TypeDef type || !type.IsValueType) return value;
			if (value.is_exact_type && value.type_names.Count == 1 && value.type_names.Contains(type.FullName)) return value;
			return symbolic_value.from_object(type.FullName, receiver.storage ?? new flow_address("constrained receiver", state.frame, call));
		}
		public MethodDef? dispatch_reference(IMethod reference, symbolic_value receiver, symbolic_state state) {
			var declaration = this.resolve(reference);
			if (declaration is not null) return declaration.IsVirtual ? this.dispatch(declaration, receiver, state, reference) : declaration;
			receiver = flow_memory.read_object(state, receiver);
			if (!receiver.is_exact_type || receiver.type_names.Count != 1 || this.type(receiver.type_names.Single()) is not TypeDef type) return null;
			if (!this.has_relation(type, reference.DeclaringType)) return null;
			foreach (var current in this.hierarchy(type)) {
				var explicit_matches = current.Methods.Where(candidate => candidate.Overrides.Any(item => this.same_method(item.MethodDeclaration, reference))).Take(2).ToArray();
				if (explicit_matches.Length == 1) return explicit_matches[0];
				var matches = current.Methods.Where(candidate => candidate.Name == reference.Name && !candidate.IsStatic && new SigComparer().Equals(call_signature.@for(candidate), call_signature.@for(reference))).Take(2).ToArray();
				if (matches.Length == 1) return matches[0];
			}
			return null;
		}
		bool has_relation(TypeDef type, ITypeDefOrRef? declaration) {
			if (declaration is null) return false;
			if (declaration is TypeSpec requested && requested.TypeSig is GenericInstSig generic) declaration = generic.GenericType.TypeDefOrRef;
			var pending = new Queue<ITypeDefOrRef>(); var visited = new HashSet<TypeDef>(); pending.Enqueue(type);
			while (pending.Count != 0 && visited.Count < 256) {
				var current = pending.Dequeue();
				if (current is TypeSpec specification && specification.TypeSig is GenericInstSig instance) current = instance.GenericType.TypeDefOrRef;
				if (new SigComparer().Equals(current, declaration)) return true;
				var resolved = this.resolve_type(current); if (resolved is null || !visited.Add(resolved)) continue;
				if (resolved.BaseType is not null) pending.Enqueue(resolved.BaseType);
				foreach (var item in resolved.Interfaces) pending.Enqueue(item.Interface);
			}
			return false;
		}
		bool same_method(IMethod left, IMethod right) => ReferenceEquals(this.resolve(left), this.resolve(right)) && this.resolve(left) is not null ||
			left.Name == right.Name && new SigComparer().Equals(left.DeclaringType, right.DeclaringType) && new SigComparer().Equals(call_signature.@for(left), call_signature.@for(right));
		public symbolic_value resolve_pointer(symbolic_value pointer, symbolic_state state) {
			if (pointer.virtual_methods.Count == 0) return pointer;
			var values = pointer.methods.Select(method => pointer.virtual_methods.Contains(method) && pointer.receiver is not null && this.dispatch(method, pointer.receiver, state) is MethodDef target ? symbolic_value.from_method(target, false, pointer.receiver) : symbolic_value.from_method(method, pointer.virtual_methods.Contains(method), pointer.receiver));
			return symbolic_value.merge(pointer.may_have_unknown_targets ? values.Append(symbolic_value.unknown_because(pointer.unknown_reason ?? "virtual pointer origin is incomplete")) : values).as_method_kind(pointer.method_kind);
		}
		public bool try_evaluate(IMethod reference, symbolic_value[] arguments, symbolic_state state, object allocation, bool constructor, out symbolic_value result) {
			result = symbolic_value.unknown;
			string type_name = call_graph_index.safe_type_name(reference.DeclaringType);
			string name = reference.Name;
			bool has_this = reference.MethodSig?.HasThis == true && !constructor;
			var instance = has_this && arguments.Length != 0 ? flow_memory.read_object(state, arguments[0]) : symbolic_value.unknown;
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			if (!framework_identity.is_framework_type(reference.DeclaringType) && !this.is_delegate(reference)) return false;
			if (type_name == "System.Object" && name == ".ctor" || type_name == "System.GC" && name == "KeepAlive") {
				if (constructor) result = symbolic_value.from_object(type_name, allocation);
				return true;
			}
			if (this.collections.try_evaluate(reference, arguments, state, allocation, constructor, out result)) return true;
			if (type_name == "System.Math" && values.Length == 1 && name is "Truncate" or "Floor" or "Ceiling" or "Round") {
				if (values[0].floating is double number) result = symbolic_value.from_floating(name switch { "Truncate" => Math.Truncate(number), "Floor" => Math.Floor(number), "Ceiling" => Math.Ceiling(number), _ => Math.Round(number, MidpointRounding.ToEven) });
				return true;
			}
			if (type_name == "System.Convert" && name == "ToInt32" && values.Length == 1 && reference.MethodSig?.Params[0].RemovePinnedAndModifiers().ElementType == ElementType.R8) {
				if (values[0].floating is double number) {
					double rounded = Math.Round(number, MidpointRounding.ToEven);
					if (!double.IsFinite(rounded) || rounded < int.MinValue || rounded > int.MaxValue) state.path_terminated = true;
					else result = symbolic_value.from_integer(this.constants.constant((int)rounded));
				}
				return true;
			}
			if (constructor && this.is_delegate(reference)) {
				var function = arguments.Length == 2 ? arguments[1] : null;
				if (function is not null) result = symbolic_value.from_delegate(type_name, allocation, this.resolve_pointer(function, state)).with_receiver(arguments.FirstOrDefault());
				return true;
			}
			if (type_name == "System.Threading.Thread" && constructor) {
				result = symbolic_value.from_object(type_name, allocation);
				if (arguments.Length != 0) state.store(new flow_address("callback", allocation, 0), arguments[0]);
				return true;
			}
			if (type_name == "System.Threading.Thread" && name == "set_IsBackground" && values.Length == 1 && reference.MethodSig?.Params[0].ElementType == ElementType.Boolean) return true;
			if (type_name == "System.Type" && name == "GetTypeFromHandle") { result = values.FirstOrDefault() ?? symbolic_value.unknown; return true; }
			if (type_name == "System.Type" && name == "get_Module" && values.Length == 0 && instance.represents_type && !instance.may_have_unknown_targets && instance.type_names.Count == 1 && this.type(instance.type_names.Single()) is TypeDef runtime_type) {
				result = symbolic_value.from_object("System.Reflection.Module", new flow_address("runtime module", runtime_type.Module, 0)); return true;
			}
			if (type_name == "System.Reflection.Module" && name == "get_FullyQualifiedName" && values.Length == 0 && instance.storage is flow_address { kind: "runtime module" }) return true;
			if (type_name == "System.Runtime.InteropServices.Marshal" && name == "GetHINSTANCE" && values.Length == 1 && values[0].storage is flow_address { kind: "runtime module" }) return true;
			if (type_name is "System.IntPtr" or "System.UIntPtr" && name == "op_Explicit" && values.Length == 1 && reference.MethodSig?.Params.Count == 1 && reference.MethodSig.RetType.RemovePinnedAndModifiers().ElementType is ElementType.Ptr or ElementType.I or ElementType.U) { result = values[0]; return true; }
			if (type_name == "System.String" && name == "get_Chars" && values.Length == 1 && reference.MethodSig?.Params[0].ElementType == ElementType.I4) {
				if (instance.strings.Count == 1 && !instance.may_have_unknown_targets && values[0].integer?.is_constant == true) {
					var text = instance.strings.Single(); long index = values[0].integer!.value;
					if (index < 0 || index >= text.Length) state.path_terminated = true; else result = symbolic_value.from_integer(this.constants.constant(text[(int)index]));
				}
				return true;
			}
			if (type_name is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" && name == "GetMethodFromHandle") { result = (values.FirstOrDefault() ?? symbolic_value.unknown).as_method_kind(method_value_kind.reflection); return true; }
			if (name == "get_MethodHandle" && type_name is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo") { result = instance.as_method_kind(method_value_kind.handle); return true; }
			if (name == "GetFunctionPointer" && type_name == "System.RuntimeMethodHandle") { result = instance.as_method_kind(method_value_kind.pointer); return true; }
			if (type_name == "System.Type" && name == "GetType" && values.Length != 0) {
				var names = values[0].strings.Select(value => value.Split(',')[0].Trim()).Where(value => this.type(value) is not null).ToArray();
				result = names.Length == 0 ? symbolic_value.unknown : symbolic_value.merge(names.Select(symbolic_value.from_type)); return true;
			}
			if (type_name == "System.Object" && name == "GetType") { result = symbolic_value.merge(instance.type_names.Select(symbolic_value.from_type)); return true; }
			if (type_name == "System.Type" && name is "GetMethod" or "GetConstructor") {
				var names = name == "GetConstructor" ? new[] { ".ctor" } : values.SelectMany(value => value.strings).Distinct(StringComparer.Ordinal).ToArray();
				var candidates = instance.type_names.Select(this.type).Where(type => type is not null).SelectMany(type => this.hierarchy(type!).SelectMany(parent => parent.Methods)).Where(method => names.Contains(method.Name.ToString(), StringComparer.Ordinal)).Distinct().ToArray();
				var parameter_types = values.FirstOrDefault(value => value.type_names.Contains("System.Type[]"));
				if (parameter_types?.storage is not null && invocation_model.array_values(parameter_types, state) is symbolic_value[] parameters) candidates = candidates.Where(method => method.MethodSig.Params.Count == parameters.Length && method.MethodSig.Params.Select((type, index) => parameters[index].type_names.Contains(type.FullName)).All(equal => equal)).ToArray();
				bool binding_flags = reference.MethodSig?.Params.Any(type => type.FullName == "System.Reflection.BindingFlags") == true;
				if (!binding_flags) candidates = candidates.Where(method => method.IsPublic).ToArray();
				else {
					int flag_index = reference.MethodSig!.Params.Select((type, index) => (type: type, index: index)).First(pair => pair.type.FullName == "System.Reflection.BindingFlags").index;
					var flags = flag_index < values.Length ? values[flag_index].integer : null;
					if (flags?.is_constant == true) candidates = candidates.Where(method => (method.IsPublic ? (flags.value & 16) != 0 : (flags.value & 32) != 0) && (method.IsStatic ? (flags.value & 8) != 0 : (flags.value & 4) != 0) &&
						((flags.value & 2) == 0 || instance.type_names.Select(this.type).Any(type => ReferenceEquals(type, method.DeclaringType)))).ToArray();
				}
				result = candidates.Length == 1 ? symbolic_value.from_method(candidates[0]).as_method_kind(method_value_kind.reflection) : candidates.Length == 0 && instance.type_names.All(name => this.type(name) is null) ? symbolic_value.from_lookup(instance.type_names, names) : symbolic_value.unknown; return true;
			}
			if (name == "MakeGenericMethod" && type_name.StartsWith("System.Reflection.", StringComparison.Ordinal)) {
				var requested = values.Length == 0 ? null : invocation_model.array_values(values[0], state);
				var methods = new List<symbolic_value>();
				foreach (var target in instance.methods) {
					if (requested is null || requested.Length != target.GenericParameters.Count) continue;
					var arguments_types = requested.Select(value => value.type_names.Count == 1 ? this.signature_type(target.Module, value.type_names.Single()) : null).ToArray();
					if (arguments_types.Any(type => type is null)) continue;
					var specification = new MethodSpecUser(target, new GenericInstMethodSig(arguments_types!));
					methods.Add(symbolic_value.from_method(target, signature: call_signature.@for(specification)).as_method_kind(method_value_kind.reflection));
				}
				result = symbolic_value.merge(methods); return true;
			}
			if (name == "CreateDelegate" && type_name is "System.Delegate" or "System.Reflection.MethodInfo") {
				var function = arguments.FirstOrDefault(value => value.methods.Count != 0);
				if (function is null) {
					var requested = values.SelectMany(value => value.type_names).Select(this.type).Where(type => type is not null);
					var names = values.SelectMany(value => value.strings).ToHashSet(StringComparer.Ordinal);
					var methods = requested.SelectMany(type => this.hierarchy(type!).SelectMany(parent => parent.Methods)).Where(method => names.Contains(method.Name)).Distinct().Take(2).ToArray();
					if (methods.Length == 1) function = symbolic_value.from_method(methods[0]);
				}
				if (function is not null) result = symbolic_value.from_delegate("System.Delegate", allocation, function).with_receiver(values.FirstOrDefault(value => value.storage is not null && value.methods.Count == 0));
				return true;
			}
			if (type_name == "System.Delegate" && name == "Combine") {
				var delegates = values.Where(value => value.integer?.is_zero != true).ToArray();
				result = symbolic_value.merge(delegates);
				if (delegates.Length != 0 && delegates.All(value => value.delegate_list is not null && !value.may_have_unknown_targets && !value.may_be_null)) result = result.with_delegate_list(delegates.SelectMany(value => value.delegate_list!).ToArray());
				return true;
			}
			if (type_name == "System.Delegate" && name == "Remove") {
				result = values.FirstOrDefault() ?? symbolic_value.unknown;
				if (values.Length >= 2 && values[0].delegate_list is not null && values[1].delegate_list is not null && !values[0].may_have_unknown_targets && !values[1].may_have_unknown_targets) {
					var entries = values[0].delegate_list!.ToList(); var remove = values[1].delegate_list!;
					for (int index = entries.Count - remove.Count; index >= 0 && remove.Count != 0; index--) if (entries.Skip(index).Take(remove.Count).Zip(remove, symbolic_value.same_binding).All(equal => equal)) { entries.RemoveRange(index, remove.Count); break; }
					result = entries.Count == 0 ? symbolic_value.from_integer(new integer_expression.factory().constant(0)) : values[0].with_delegate_list(entries);
				}
				else if (values.Length >= 2 && ReferenceEquals(values[0], values[1])) result = symbolic_value.from_integer(new integer_expression.factory().constant(0));
				return true;
			}
			if (type_name == "System.Threading.Interlocked" && name is "Exchange" or "CompareExchange" && values.Length >= 2 && values[0].storage is object address) {
				result = state.load(address);
				if (name == "Exchange") state.store(address, values[1]);
				else if (values.Length >= 3) {
				if (result.same_as(values[2]) || result.integer is integer_expression previous && previous.is_constant && values[2].integer is integer_expression expected && expected.is_constant && previous.value == expected.value) state.store(address, values[1]);
					else if (result.integer?.is_constant != true || values[2].integer?.is_constant != true) state.store(address, symbolic_value.merge(new[] { result, values[1] }));
				}
				return true;
			}
			if (type_name == "System.Runtime.InteropServices.Marshal" && name.StartsWith("GetDelegateForFunctionPointer", StringComparison.Ordinal)) { result = values.FirstOrDefault(value => value.methods.Count != 0) ?? symbolic_value.unknown; return true; }
			if (type_name == "System.Runtime.InteropServices.Marshal" && name.StartsWith("GetFunctionPointerForDelegate", StringComparison.Ordinal)) { result = (values.FirstOrDefault() ?? symbolic_value.unknown).as_method_kind(method_value_kind.pointer); return true; }
			if (type_name == "Microsoft.CSharp.RuntimeBinder.Binder" && name == "InvokeMember") { result = symbolic_value.dynamic(values.SelectMany(value => value.strings)); return true; }
			if (type_name.StartsWith("System.Runtime.CompilerServices.CallSite`", StringComparison.Ordinal) && name == "Create") { result = values.FirstOrDefault() ?? symbolic_value.unknown; return true; }
			var invocations = this.targets(reference, arguments, state).ToArray();
			if (invocations.Length != 0 && name is "Invoke" or "DynamicInvoke") {
				if (instance.delegate_list is not null && !instance.may_have_unknown_targets && !instance.is_ambiguous) {
					foreach (var target in invocations) { result = this.evaluate(target.method, target.arguments, state); if (state.path_terminated) break; }
				}
				else {
					var results = new List<symbolic_value>(); symbolic_state? normal = null, exceptional = null;
					foreach (var target in invocations) {
						var branch = state.clone(); results.Add(this.evaluate(target.method, target.arguments, branch));
						if (branch.path_terminated) { if (exceptional is null) exceptional = branch; else exceptional.merge_from(branch); }
						else if (normal is null) normal = branch; else normal.merge_from(branch);
					}
					if (instance.may_have_unknown_targets) { results.Add(symbolic_value.unknown); var branch = state.clone(); context_call_evaluator.invalidate_unknown_call(branch, arguments); if (normal is null) normal = branch; else normal.merge_from(branch); }
					if ((normal ?? exceptional) is symbolic_state exit) state.import_memory(exit);
					state.path_terminated = normal is null && exceptional is not null;
					result = symbolic_value.merge(results);
				}
				return true;
			}
			return false;
		}
		public IEnumerable<invocation_target> targets(IMethod reference, symbolic_value[] arguments, symbolic_state state) {
			if (!framework_identity.is_framework_type(reference.DeclaringType) && !this.is_delegate(reference)) yield break;
			string type_name = call_graph_index.safe_type_name(reference.DeclaringType); string name = reference.Name;
			bool has_this = reference.MethodSig?.HasThis == true;
			var instance = has_this && arguments.Length != 0 ? arguments[0] : symbolic_value.unknown;
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			bool reflection = name == "Invoke" && type_name.StartsWith("System.Reflection.", StringComparison.Ordinal);
			if (reflection || name == "DynamicInvoke" && type_name == "System.Delegate") {
				var array = values.LastOrDefault(value => value.type_names.Contains("System.Object[]"));
				var actual = array is null ? Array.Empty<symbolic_value>() : invocation_model.array_values(array, state);
				foreach (var target in instance.methods) {
					var parameters = actual ?? Enumerable.Repeat(symbolic_value.unknown, target.MethodSig.Params.Count).ToArray();
					if (parameters.Length != target.MethodSig.Params.Count) continue;
					if (!target.IsStatic) parameters = new[] { reflection ? values.FirstOrDefault() ?? symbolic_value.unknown : instance.receiver ?? symbolic_value.unknown }.Concat(parameters).ToArray();
					yield return new invocation_target(target, parameters, reflection ? "reflection invoke" : "delegate invoke", instance.is_ambiguous);
				}
				yield break;
			}
			if (name == "Invoke" && instance.methods.Count != 0 && (this.is_delegate(reference) || type_name == "System.Delegate" || instance.type_names.Any(value => value == "System.Delegate"))) {
				foreach (var entry in instance.delegate_list ?? instance.methods.Select(method => new delegate_entry(method, instance.receiver)).ToArray()) {
					var target = entry.method;
					var parameters = values;
					if (target.IsStatic && parameters.Length == target.MethodSig.Params.Count) { }
					else if (!target.IsStatic && parameters.Length == target.MethodSig.Params.Count) parameters = new[] { entry.receiver ?? symbolic_value.unknown }.Concat(parameters).ToArray();
					else if (!target.IsStatic && parameters.Length == target.MethodSig.Params.Count + 1) { }
					else if (target.IsStatic && entry.receiver is not null && parameters.Length + 1 == target.MethodSig.Params.Count) parameters = new[] { entry.receiver }.Concat(parameters).ToArray();
					else continue;
					yield return new invocation_target(target, parameters, "delegate invoke", instance.is_ambiguous || instance.virtual_methods.Count != 0);
				}
			}
			if (name == "Invoke" && instance.dynamic_names.Count != 0 && values.Length >= 2) {
				var receiver = flow_memory.read_object(state, values[1]);
				foreach (var type in receiver.type_names.Select(this.type).Where(type => type is not null)) {
					var candidates = this.hierarchy(type!).SelectMany(parent => parent.Methods).Where(method => !method.IsStatic && instance.dynamic_names.Contains(method.Name) && method.MethodSig.Params.Count == values.Length - 2).Distinct().Take(2).ToArray();
					if (candidates.Length == 1) yield return new invocation_target(candidates[0], new[] { receiver }.Concat(values.Skip(2)).ToArray(), "dynamic invoke", !receiver.is_exact_type);
				}
			}
			if (name == "Start" && type_name.StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal) && reference is MethodSpec specification) {
				foreach (var type_signature in specification.GenericInstMethodSig.GenericArguments) {
					var type = this.resolve_type(type_signature.ToTypeDefOrRef());
					var move_next = type?.Methods.FirstOrDefault(method => method.Name == "MoveNext" && method.MethodSig.Params.Count == 0);
					if (move_next is not null) yield return new invocation_target(move_next, new[] { values.FirstOrDefault() ?? symbolic_value.unknown }, "state machine", false);
				}
			}
			symbolic_value? callback = null;
			if (type_name == "System.Threading.Thread" && name == "Start" && instance.storage is not null) callback = state.load(new flow_address("callback", instance.storage, 0));
			if (type_name == "System.Threading.ThreadPool" && name.Contains("Queue", StringComparison.Ordinal) || type_name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) && name is "Run" or "StartNew" or "ContinueWith") callback = values.FirstOrDefault(value => value.methods.Count != 0);
			if (callback is not null) foreach (var target in callback.methods) {
				var parameters = Enumerable.Repeat(symbolic_value.unknown, target.MethodSig.Params.Count).ToArray();
				if (!target.IsStatic) parameters = new[] { callback.receiver ?? symbolic_value.unknown }.Concat(parameters).ToArray();
				yield return new invocation_target(target, parameters, "scheduled callback", true);
			}
		}
		IEnumerable<TypeDef> hierarchy(TypeDef type) { var visited = new HashSet<TypeDef>(); for (var current = type; current is not null && visited.Add(current); current = this.resolve_type(current.BaseType)) yield return current; }
		TypeSig? signature_type(ModuleDef module, string name) => name switch {
			"System.Void" => module.CorLibTypes.Void, "System.Boolean" => module.CorLibTypes.Boolean,
			"System.Int32" => module.CorLibTypes.Int32, "System.UInt32" => module.CorLibTypes.UInt32,
			"System.Int64" => module.CorLibTypes.Int64, "System.UInt64" => module.CorLibTypes.UInt64,
			"System.String" => module.CorLibTypes.String, "System.Object" => module.CorLibTypes.Object,
			"System.IntPtr" => module.CorLibTypes.IntPtr, "System.UIntPtr" => module.CorLibTypes.UIntPtr,
			"System.Byte" => module.CorLibTypes.Byte, "System.SByte" => module.CorLibTypes.SByte,
			"System.Int16" => module.CorLibTypes.Int16, "System.UInt16" => module.CorLibTypes.UInt16,
			"System.Single" => module.CorLibTypes.Single, "System.Double" => module.CorLibTypes.Double,
			"System.Char" => module.CorLibTypes.Char, _ => this.type(name)?.ToTypeSig(),
		};
		static symbolic_value[]? array_values(symbolic_value array, symbolic_state state) {
			if (array.storage is null || state.load(new flow_address("length", array.storage, 0)).integer is not integer_expression length || !length.is_constant || length.value < 0 || length.value > 1024) return null;
			return Enumerable.Range(0, (int)length.value).Select(index => state.load(new flow_address("element", array.storage, (long)index))).ToArray();
		}
	}
}
