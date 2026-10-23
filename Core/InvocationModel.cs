using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed record InvocationTarget(MethodDef Method, SymbolicValue[] Arguments, string Operation, bool Potential);

	sealed class InvocationModel {
		readonly Dictionary<string, TypeDef[]> types;
		readonly Func<IMethod, MethodDef?> resolve;
		readonly Func<MethodDef, IReadOnlyList<SymbolicValue>, SymbolicState, SymbolicValue> evaluate;
		readonly Dictionary<(MethodDef, TypeDef), MethodDef?> dispatch_cache = new Dictionary<(MethodDef, TypeDef), MethodDef?>();
		readonly CollectionFlowModel collections = new CollectionFlowModel();
		readonly IntegerExpression.Factory constants = new IntegerExpression.Factory();
		public InvocationModel(IEnumerable<ModuleDef> modules, Func<IMethod, MethodDef?> resolve,
			Func<MethodDef, IReadOnlyList<SymbolicValue>, SymbolicState, SymbolicValue> evaluate) {
			types = modules.SelectMany(module => module.GetTypes()).GroupBy(type => type.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			this.resolve = resolve; this.evaluate = evaluate;
		}
		public TypeDef? Type(string name) => types.TryGetValue(name, out var values) && values.Length == 1 ? values[0] : null;
		public bool IsFrameworkModel(IMethod reference) {
			string type = CallGraphIndex.SafeTypeName(reference.DeclaringType);
			return FrameworkIdentity.IsFrameworkType(reference.DeclaringType) && (type.StartsWith("System.", StringComparison.Ordinal) || type.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal));
		}
		public TypeDef? ResolveType(ITypeDefOrRef? reference) {
			if (reference is TypeSpec specification && specification.TypeSig is GenericInstSig instance) reference = instance.GenericType.TypeDefOrRef;
			if (reference is TypeDef definition) return definition;
			try { if (reference?.ResolveTypeDef() is TypeDef resolved) return resolved; } catch (Exception) { }
			var candidate = reference is null ? null : Type(reference.FullName);
			return candidate is not null && (reference?.DefinitionAssembly is null || StringComparer.OrdinalIgnoreCase.Equals(candidate.DefinitionAssembly?.FullName, reference.DefinitionAssembly.FullName)) ? candidate : null;
		}
		public bool IsDelegate(IMethod reference) {
			return IsDelegateType(reference.DeclaringType);
		}
		public bool IsDelegateType(ITypeDefOrRef? reference) {
			string name = CallGraphIndex.SafeTypeName(reference);
			if (FrameworkIdentity.IsFrameworkType(reference) && name is "System.Threading.ThreadStart" or "System.Threading.ParameterizedThreadStart" or "System.Threading.WaitCallback" or "System.Threading.WaitOrTimerCallback" or "System.Threading.TimerCallback") return true;
			if (FrameworkIdentity.IsFrameworkType(reference) && (name == "System.Action" || name.StartsWith("System.Action`", StringComparison.Ordinal) || name.StartsWith("System.Func`", StringComparison.Ordinal) || name.StartsWith("System.Predicate`", StringComparison.Ordinal) || name == "System.EventHandler" || name.StartsWith("System.EventHandler`", StringComparison.Ordinal) || name.StartsWith("System.Comparison`", StringComparison.Ordinal))) return true;
			var type = ResolveType(reference);
			return CallGraphIndex.SafeTypeName(type?.BaseType) is "System.MulticastDelegate" or "System.Delegate";
		}
		public MethodDef? Dispatch(MethodDef declaration, SymbolicValue receiver, SymbolicState state) {
			receiver = FlowMemory.ReadObject(state, receiver);
			if (!receiver.IsExactType || receiver.TypeNames.Count != 1 || Type(receiver.TypeNames.Single()) is not TypeDef type) return null;
			if (dispatch_cache.TryGetValue((declaration, type), out var cached)) return cached;
			var relations = new Queue<TypeDef>(); var related_types = new HashSet<TypeDef>(); relations.Enqueue(type);
			while (relations.Count != 0) {
				var current = relations.Dequeue(); if (!related_types.Add(current)) continue;
				if (ResolveType(current.BaseType) is TypeDef parent) relations.Enqueue(parent);
				foreach (var implementation in current.Interfaces) if (ResolveType(implementation.Interface) is TypeDef implemented) relations.Enqueue(implemented);
			}
			if (!related_types.Contains(declaration.DeclaringType)) return dispatch_cache[(declaration, type)] = null;
			MethodDef? result = null;
			var visited = new HashSet<TypeDef>();
			bool member_of_type = false;
			for (var current = type; current is not null && visited.Add(current); current = ResolveType(current.BaseType)) {
				if (ReferenceEquals(current, declaration.DeclaringType)) member_of_type = true;
				var explicit_method = current.Methods.FirstOrDefault(candidate => candidate.Overrides.Any(item => SameMethod(item.MethodDeclaration, declaration)));
				if (explicit_method is not null) { result = explicit_method; break; }
				var match = current.Methods.FirstOrDefault(candidate => candidate.Name == declaration.Name && !candidate.IsStatic &&
					new SigComparer().Equals(candidate.MethodSig, declaration.MethodSig) && (declaration.DeclaringType.IsInterface || candidate.IsVirtual && !candidate.IsNewSlot || ReferenceEquals(candidate, declaration)));
				if (match is not null) { result = match; break; }
			}
			if (result is null && member_of_type && !declaration.IsAbstract) result = declaration;
			return dispatch_cache[(declaration, type)] = result;
		}
		public MethodDef? DispatchReference(IMethod reference, SymbolicValue receiver, SymbolicState state) {
			var declaration = resolve(reference);
			if (declaration is not null) return declaration.IsVirtual ? Dispatch(declaration, receiver, state) : declaration;
			receiver = FlowMemory.ReadObject(state, receiver);
			if (!receiver.IsExactType || receiver.TypeNames.Count != 1 || Type(receiver.TypeNames.Single()) is not TypeDef type) return null;
			if (!HasRelation(type, reference.DeclaringType)) return null;
			foreach (var current in Hierarchy(type)) {
				var explicit_matches = current.Methods.Where(candidate => candidate.Overrides.Any(item => SameMethod(item.MethodDeclaration, reference))).Take(2).ToArray();
				if (explicit_matches.Length == 1) return explicit_matches[0];
				var matches = current.Methods.Where(candidate => candidate.Name == reference.Name && !candidate.IsStatic && new SigComparer().Equals(CallSignature.For(candidate), CallSignature.For(reference))).Take(2).ToArray();
				if (matches.Length == 1) return matches[0];
			}
			return null;
		}
		bool HasRelation(TypeDef type, ITypeDefOrRef? declaration) {
			if (declaration is null) return false;
			if (declaration is TypeSpec requested && requested.TypeSig is GenericInstSig generic) declaration = generic.GenericType.TypeDefOrRef;
			var pending = new Queue<ITypeDefOrRef>(); var visited = new HashSet<TypeDef>(); pending.Enqueue(type);
			while (pending.Count != 0 && visited.Count < 256) {
				var current = pending.Dequeue();
				if (current is TypeSpec specification && specification.TypeSig is GenericInstSig instance) current = instance.GenericType.TypeDefOrRef;
				if (new SigComparer().Equals(current, declaration)) return true;
				var resolved = ResolveType(current); if (resolved is null || !visited.Add(resolved)) continue;
				if (resolved.BaseType is not null) pending.Enqueue(resolved.BaseType);
				foreach (var item in resolved.Interfaces) pending.Enqueue(item.Interface);
			}
			return false;
		}
		bool SameMethod(IMethod left, IMethod right) => ReferenceEquals(resolve(left), resolve(right)) && resolve(left) is not null ||
			left.Name == right.Name && new SigComparer().Equals(left.DeclaringType, right.DeclaringType) && new SigComparer().Equals(CallSignature.For(left), CallSignature.For(right));
		public SymbolicValue ResolvePointer(SymbolicValue pointer, SymbolicState state) {
			if (pointer.VirtualMethods.Count == 0) return pointer;
			var values = pointer.Methods.Select(method => pointer.VirtualMethods.Contains(method) && pointer.Receiver is not null && Dispatch(method, pointer.Receiver, state) is MethodDef target ? SymbolicValue.FromMethod(target, false, pointer.Receiver) : SymbolicValue.FromMethod(method, pointer.VirtualMethods.Contains(method), pointer.Receiver));
			return SymbolicValue.Merge(pointer.MayHaveUnknownTargets ? values.Append(SymbolicValue.UnknownBecause(pointer.UnknownReason ?? "virtual pointer origin is incomplete")) : values).AsMethodKind(pointer.MethodKind);
		}
		public bool TryEvaluate(IMethod reference, SymbolicValue[] arguments, SymbolicState state, object allocation, bool constructor, out SymbolicValue result) {
			result = SymbolicValue.Unknown;
			string type_name = CallGraphIndex.SafeTypeName(reference.DeclaringType);
			string name = reference.Name;
			bool has_this = reference.MethodSig?.HasThis == true && !constructor;
			var instance = has_this && arguments.Length != 0 ? FlowMemory.ReadObject(state, arguments[0]) : SymbolicValue.Unknown;
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			if (!FrameworkIdentity.IsFrameworkType(reference.DeclaringType) && !IsDelegate(reference)) return false;
			if (type_name == "System.Object" && name == ".ctor" || type_name == "System.GC" && name == "KeepAlive") {
				if (constructor) result = SymbolicValue.FromObject(type_name, allocation);
				return true;
			}
			if (collections.TryEvaluate(reference, arguments, state, allocation, constructor, out result)) return true;
			if (type_name == "System.Math" && values.Length == 1 && name is "Truncate" or "Floor" or "Ceiling" or "Round") {
				if (values[0].Floating is double number) result = SymbolicValue.FromFloating(name switch { "Truncate" => Math.Truncate(number), "Floor" => Math.Floor(number), "Ceiling" => Math.Ceiling(number), _ => Math.Round(number, MidpointRounding.ToEven) });
				return true;
			}
			if (type_name == "System.Convert" && name == "ToInt32" && values.Length == 1 && reference.MethodSig?.Params[0].RemovePinnedAndModifiers().ElementType == ElementType.R8) {
				if (values[0].Floating is double number) {
					double rounded = Math.Round(number, MidpointRounding.ToEven);
					if (!double.IsFinite(rounded) || rounded < int.MinValue || rounded > int.MaxValue) state.PathTerminated = true;
					else result = SymbolicValue.FromInteger(constants.Constant((int)rounded));
				}
				return true;
			}
			if (constructor && IsDelegate(reference)) {
				var function = arguments.Length == 2 ? arguments[1] : null;
				if (function is not null) result = SymbolicValue.FromDelegate(type_name, allocation, ResolvePointer(function, state)).WithReceiver(arguments.FirstOrDefault());
				return true;
			}
			if (type_name == "System.Threading.Thread" && constructor) {
				result = SymbolicValue.FromObject(type_name, allocation);
				if (arguments.Length != 0) state.Store(new FlowAddress("callback", allocation, 0), arguments[0]);
				return true;
			}
			if (type_name == "System.Threading.Thread" && name == "set_IsBackground" && values.Length == 1 && reference.MethodSig?.Params[0].ElementType == ElementType.Boolean) return true;
			if (type_name == "System.Type" && name == "GetTypeFromHandle") { result = values.FirstOrDefault() ?? SymbolicValue.Unknown; return true; }
			if (type_name == "System.Type" && name == "get_Module" && values.Length == 0 && instance.RepresentsType && !instance.MayHaveUnknownTargets && instance.TypeNames.Count == 1 && Type(instance.TypeNames.Single()) is TypeDef runtime_type) {
				result = SymbolicValue.FromObject("System.Reflection.Module", new FlowAddress("runtime module", runtime_type.Module, 0)); return true;
			}
			if (type_name == "System.Reflection.Module" && name == "get_FullyQualifiedName" && values.Length == 0 && instance.Storage is FlowAddress { Kind: "runtime module" }) return true;
			if (type_name == "System.Runtime.InteropServices.Marshal" && name == "GetHINSTANCE" && values.Length == 1 && values[0].Storage is FlowAddress { Kind: "runtime module" }) return true;
			if (type_name is "System.IntPtr" or "System.UIntPtr" && name == "op_Explicit" && values.Length == 1 && reference.MethodSig?.Params.Count == 1 && reference.MethodSig.RetType.RemovePinnedAndModifiers().ElementType is ElementType.Ptr or ElementType.I or ElementType.U) { result = values[0]; return true; }
			if (type_name == "System.String" && name == "get_Chars" && values.Length == 1 && reference.MethodSig?.Params[0].ElementType == ElementType.I4) {
				if (instance.Strings.Count == 1 && !instance.MayHaveUnknownTargets && values[0].Integer?.IsConstant == true) {
					var text = instance.Strings.Single(); long index = values[0].Integer!.Value;
					if (index < 0 || index >= text.Length) state.PathTerminated = true; else result = SymbolicValue.FromInteger(constants.Constant(text[(int)index]));
				}
				return true;
			}
			if (type_name is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo" && name == "GetMethodFromHandle") { result = (values.FirstOrDefault() ?? SymbolicValue.Unknown).AsMethodKind(MethodValueKind.Reflection); return true; }
			if (name == "get_MethodHandle" && type_name is "System.Reflection.MethodBase" or "System.Reflection.MethodInfo") { result = instance.AsMethodKind(MethodValueKind.Handle); return true; }
			if (name == "GetFunctionPointer" && type_name == "System.RuntimeMethodHandle") { result = instance.AsMethodKind(MethodValueKind.Pointer); return true; }
			if (type_name == "System.Type" && name == "GetType" && values.Length != 0) {
				var names = values[0].Strings.Select(value => value.Split(',')[0].Trim()).Where(value => Type(value) is not null).ToArray();
				result = names.Length == 0 ? SymbolicValue.Unknown : SymbolicValue.Merge(names.Select(SymbolicValue.FromType)); return true;
			}
			if (type_name == "System.Object" && name == "GetType") { result = SymbolicValue.Merge(instance.TypeNames.Select(SymbolicValue.FromType)); return true; }
			if (type_name == "System.Type" && name is "GetMethod" or "GetConstructor") {
				var names = name == "GetConstructor" ? new[] { ".ctor" } : values.SelectMany(value => value.Strings).Distinct(StringComparer.Ordinal).ToArray();
				var candidates = instance.TypeNames.Select(Type).Where(type => type is not null).SelectMany(type => Hierarchy(type!).SelectMany(parent => parent.Methods)).Where(method => names.Contains(method.Name.ToString(), StringComparer.Ordinal)).Distinct().ToArray();
				var parameter_types = values.FirstOrDefault(value => value.TypeNames.Contains("System.Type[]"));
				if (parameter_types?.Storage is not null && ArrayValues(parameter_types, state) is SymbolicValue[] parameters) candidates = candidates.Where(method => method.MethodSig.Params.Count == parameters.Length && method.MethodSig.Params.Select((type, index) => parameters[index].TypeNames.Contains(type.FullName)).All(equal => equal)).ToArray();
				bool binding_flags = reference.MethodSig?.Params.Any(type => type.FullName == "System.Reflection.BindingFlags") == true;
				if (!binding_flags) candidates = candidates.Where(method => method.IsPublic).ToArray();
				else {
					int flag_index = reference.MethodSig!.Params.Select((type, index) => (type, index)).First(pair => pair.type.FullName == "System.Reflection.BindingFlags").index;
					var flags = flag_index < values.Length ? values[flag_index].Integer : null;
					if (flags?.IsConstant == true) candidates = candidates.Where(method => (method.IsPublic ? (flags.Value & 16) != 0 : (flags.Value & 32) != 0) && (method.IsStatic ? (flags.Value & 8) != 0 : (flags.Value & 4) != 0)).ToArray();
				}
				result = candidates.Length == 1 ? SymbolicValue.FromMethod(candidates[0]).AsMethodKind(MethodValueKind.Reflection) : candidates.Length == 0 && instance.TypeNames.All(name => Type(name) is null) ? SymbolicValue.FromLookup(instance.TypeNames, names) : SymbolicValue.Unknown; return true;
			}
			if (name == "MakeGenericMethod" && type_name.StartsWith("System.Reflection.", StringComparison.Ordinal)) {
				var requested = values.Length == 0 ? null : ArrayValues(values[0], state);
				var methods = new List<SymbolicValue>();
				foreach (var target in instance.Methods) {
					if (requested is null || requested.Length != target.GenericParameters.Count) continue;
					var arguments_types = requested.Select(value => value.TypeNames.Count == 1 ? SignatureType(target.Module, value.TypeNames.Single()) : null).ToArray();
					if (arguments_types.Any(type => type is null)) continue;
					var specification = new MethodSpecUser(target, new GenericInstMethodSig(arguments_types!));
					methods.Add(SymbolicValue.FromMethod(target, signature: CallSignature.For(specification)).AsMethodKind(MethodValueKind.Reflection));
				}
				result = SymbolicValue.Merge(methods); return true;
			}
			if (name == "CreateDelegate" && type_name is "System.Delegate" or "System.Reflection.MethodInfo") {
				var function = arguments.FirstOrDefault(value => value.Methods.Count != 0);
				if (function is null) {
					var requested = values.SelectMany(value => value.TypeNames).Select(Type).Where(type => type is not null);
					var names = values.SelectMany(value => value.Strings).ToHashSet(StringComparer.Ordinal);
					var methods = requested.SelectMany(type => Hierarchy(type!).SelectMany(parent => parent.Methods)).Where(method => names.Contains(method.Name)).Distinct().Take(2).ToArray();
					if (methods.Length == 1) function = SymbolicValue.FromMethod(methods[0]);
				}
				if (function is not null) result = SymbolicValue.FromDelegate("System.Delegate", allocation, function).WithReceiver(values.FirstOrDefault(value => value.Storage is not null && value.Methods.Count == 0));
				return true;
			}
			if (type_name == "System.Delegate" && name == "Combine") {
				var delegates = values.Where(value => value.Integer?.IsZero != true).ToArray();
				result = SymbolicValue.Merge(delegates);
				if (delegates.Length != 0 && delegates.All(value => value.DelegateList is not null && !value.MayHaveUnknownTargets && !value.MayBeNull)) result = result.WithDelegateList(delegates.SelectMany(value => value.DelegateList!).ToArray());
				return true;
			}
			if (type_name == "System.Delegate" && name == "Remove") {
				result = values.FirstOrDefault() ?? SymbolicValue.Unknown;
				if (values.Length >= 2 && values[0].DelegateList is not null && values[1].DelegateList is not null && !values[0].MayHaveUnknownTargets && !values[1].MayHaveUnknownTargets) {
					var entries = values[0].DelegateList!.ToList(); var remove = values[1].DelegateList!;
					for (int index = entries.Count - remove.Count; index >= 0 && remove.Count != 0; index--) if (entries.Skip(index).Take(remove.Count).Zip(remove, SymbolicValue.SameBinding).All(equal => equal)) { entries.RemoveRange(index, remove.Count); break; }
					result = entries.Count == 0 ? SymbolicValue.FromInteger(new IntegerExpression.Factory().Constant(0)) : values[0].WithDelegateList(entries);
				}
				else if (values.Length >= 2 && ReferenceEquals(values[0], values[1])) result = SymbolicValue.FromInteger(new IntegerExpression.Factory().Constant(0));
				return true;
			}
			if (type_name == "System.Threading.Interlocked" && name is "Exchange" or "CompareExchange" && values.Length >= 2 && values[0].Storage is object address) {
				result = state.Load(address);
				if (name == "Exchange") state.Store(address, values[1]);
				else if (values.Length >= 3) {
				if (result.SameAs(values[2]) || result.Integer is IntegerExpression previous && previous.IsConstant && values[2].Integer is IntegerExpression expected && expected.IsConstant && previous.Value == expected.Value) state.Store(address, values[1]);
					else if (result.Integer?.IsConstant != true || values[2].Integer?.IsConstant != true) state.Store(address, SymbolicValue.Merge(new[] { result, values[1] }));
				}
				return true;
			}
			if (type_name == "System.Runtime.InteropServices.Marshal" && name.StartsWith("GetDelegateForFunctionPointer", StringComparison.Ordinal)) { result = values.FirstOrDefault(value => value.Methods.Count != 0) ?? SymbolicValue.Unknown; return true; }
			if (type_name == "System.Runtime.InteropServices.Marshal" && name.StartsWith("GetFunctionPointerForDelegate", StringComparison.Ordinal)) { result = (values.FirstOrDefault() ?? SymbolicValue.Unknown).AsMethodKind(MethodValueKind.Pointer); return true; }
			if (type_name == "Microsoft.CSharp.RuntimeBinder.Binder" && name == "InvokeMember") { result = SymbolicValue.Dynamic(values.SelectMany(value => value.Strings)); return true; }
			if (type_name.StartsWith("System.Runtime.CompilerServices.CallSite`", StringComparison.Ordinal) && name == "Create") { result = values.FirstOrDefault() ?? SymbolicValue.Unknown; return true; }
			var invocations = Targets(reference, arguments, state).ToArray();
			if (invocations.Length != 0 && name is "Invoke" or "DynamicInvoke") {
				if (instance.DelegateList is not null && !instance.MayHaveUnknownTargets && !instance.IsAmbiguous) {
					foreach (var target in invocations) { result = evaluate(target.Method, target.Arguments, state); if (state.PathTerminated) break; }
				}
				else {
					var results = new List<SymbolicValue>(); SymbolicState? normal = null, exceptional = null;
					foreach (var target in invocations) {
						var branch = state.Clone(); results.Add(evaluate(target.Method, target.Arguments, branch));
						if (branch.PathTerminated) { if (exceptional is null) exceptional = branch; else exceptional.MergeFrom(branch); }
						else if (normal is null) normal = branch; else normal.MergeFrom(branch);
					}
					if (instance.MayHaveUnknownTargets) { results.Add(SymbolicValue.Unknown); var branch = state.Clone(); ContextCallEvaluator.InvalidateUnknownCall(branch, arguments); if (normal is null) normal = branch; else normal.MergeFrom(branch); }
					if ((normal ?? exceptional) is SymbolicState exit) state.ImportMemory(exit);
					state.PathTerminated = normal is null && exceptional is not null;
					result = SymbolicValue.Merge(results);
				}
				return true;
			}
			return false;
		}
		public IEnumerable<InvocationTarget> Targets(IMethod reference, SymbolicValue[] arguments, SymbolicState state) {
			if (!FrameworkIdentity.IsFrameworkType(reference.DeclaringType) && !IsDelegate(reference)) yield break;
			string type_name = CallGraphIndex.SafeTypeName(reference.DeclaringType); string name = reference.Name;
			bool has_this = reference.MethodSig?.HasThis == true;
			var instance = has_this && arguments.Length != 0 ? arguments[0] : SymbolicValue.Unknown;
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			bool reflection = name == "Invoke" && type_name.StartsWith("System.Reflection.", StringComparison.Ordinal);
			if (reflection || name == "DynamicInvoke" && type_name == "System.Delegate") {
				var array = values.LastOrDefault(value => value.TypeNames.Contains("System.Object[]"));
				var actual = array is null ? Array.Empty<SymbolicValue>() : ArrayValues(array, state);
				foreach (var target in instance.Methods) {
					var parameters = actual ?? Enumerable.Repeat(SymbolicValue.Unknown, target.MethodSig.Params.Count).ToArray();
					if (parameters.Length != target.MethodSig.Params.Count) continue;
					if (!target.IsStatic) parameters = new[] { reflection ? values.FirstOrDefault() ?? SymbolicValue.Unknown : instance.Receiver ?? SymbolicValue.Unknown }.Concat(parameters).ToArray();
					yield return new InvocationTarget(target, parameters, reflection ? "reflection invoke" : "delegate invoke", instance.IsAmbiguous);
				}
				yield break;
			}
			if (name == "Invoke" && instance.Methods.Count != 0 && (IsDelegate(reference) || type_name == "System.Delegate" || instance.TypeNames.Any(value => value == "System.Delegate"))) {
				foreach (var entry in instance.DelegateList ?? instance.Methods.Select(method => new DelegateEntry(method, instance.Receiver)).ToArray()) {
					var target = entry.Method;
					var parameters = values;
					if (target.IsStatic && parameters.Length == target.MethodSig.Params.Count) { }
					else if (!target.IsStatic && parameters.Length == target.MethodSig.Params.Count) parameters = new[] { entry.Receiver ?? SymbolicValue.Unknown }.Concat(parameters).ToArray();
					else if (!target.IsStatic && parameters.Length == target.MethodSig.Params.Count + 1) { }
					else if (target.IsStatic && entry.Receiver is not null && parameters.Length + 1 == target.MethodSig.Params.Count) parameters = new[] { entry.Receiver }.Concat(parameters).ToArray();
					else continue;
					yield return new InvocationTarget(target, parameters, "delegate invoke", instance.IsAmbiguous || instance.VirtualMethods.Count != 0);
				}
			}
			if (name == "Invoke" && instance.DynamicNames.Count != 0 && values.Length >= 2) {
				var receiver = FlowMemory.ReadObject(state, values[1]);
				foreach (var type in receiver.TypeNames.Select(Type).Where(type => type is not null)) {
					var candidates = Hierarchy(type!).SelectMany(parent => parent.Methods).Where(method => !method.IsStatic && instance.DynamicNames.Contains(method.Name) && method.MethodSig.Params.Count == values.Length - 2).Distinct().Take(2).ToArray();
					if (candidates.Length == 1) yield return new InvocationTarget(candidates[0], new[] { receiver }.Concat(values.Skip(2)).ToArray(), "dynamic invoke", !receiver.IsExactType);
				}
			}
			if (name == "Start" && type_name.StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal) && reference is MethodSpec specification) {
				foreach (var type_signature in specification.GenericInstMethodSig.GenericArguments) {
					var type = ResolveType(type_signature.ToTypeDefOrRef());
					var move_next = type?.Methods.FirstOrDefault(method => method.Name == "MoveNext" && method.MethodSig.Params.Count == 0);
					if (move_next is not null) yield return new InvocationTarget(move_next, new[] { values.FirstOrDefault() ?? SymbolicValue.Unknown }, "state machine", false);
				}
			}
			SymbolicValue? callback = null;
			if (type_name == "System.Threading.Thread" && name == "Start" && instance.Storage is not null) callback = state.Load(new FlowAddress("callback", instance.Storage, 0));
			if (type_name == "System.Threading.ThreadPool" && name.Contains("Queue", StringComparison.Ordinal) || type_name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) && name is "Run" or "StartNew" or "ContinueWith") callback = values.FirstOrDefault(value => value.Methods.Count != 0);
			if (callback is not null) foreach (var target in callback.Methods) {
				var parameters = Enumerable.Repeat(SymbolicValue.Unknown, target.MethodSig.Params.Count).ToArray();
				if (!target.IsStatic) parameters = new[] { callback.Receiver ?? SymbolicValue.Unknown }.Concat(parameters).ToArray();
				yield return new InvocationTarget(target, parameters, "scheduled callback", true);
			}
		}
		IEnumerable<TypeDef> Hierarchy(TypeDef type) { var visited = new HashSet<TypeDef>(); for (var current = type; current is not null && visited.Add(current); current = ResolveType(current.BaseType)) yield return current; }
		TypeSig? SignatureType(ModuleDef module, string name) => name switch {
			"System.Void" => module.CorLibTypes.Void, "System.Boolean" => module.CorLibTypes.Boolean,
			"System.Int32" => module.CorLibTypes.Int32, "System.UInt32" => module.CorLibTypes.UInt32,
			"System.Int64" => module.CorLibTypes.Int64, "System.UInt64" => module.CorLibTypes.UInt64,
			"System.String" => module.CorLibTypes.String, "System.Object" => module.CorLibTypes.Object,
			"System.IntPtr" => module.CorLibTypes.IntPtr, "System.UIntPtr" => module.CorLibTypes.UIntPtr,
			"System.Byte" => module.CorLibTypes.Byte, "System.SByte" => module.CorLibTypes.SByte,
			"System.Int16" => module.CorLibTypes.Int16, "System.UInt16" => module.CorLibTypes.UInt16,
			"System.Single" => module.CorLibTypes.Single, "System.Double" => module.CorLibTypes.Double,
			"System.Char" => module.CorLibTypes.Char, _ => Type(name)?.ToTypeSig(),
		};
		static SymbolicValue[]? ArrayValues(SymbolicValue array, SymbolicState state) {
			if (array.Storage is null || state.Load(new FlowAddress("length", array.Storage, 0)).Integer is not IntegerExpression length || !length.IsConstant || length.Value < 0 || length.Value > 1024) return null;
			return Enumerable.Range(0, (int)length.Value).Select(index => state.Load(new FlowAddress("element", array.Storage, (long)index))).ToArray();
		}
	}
}
