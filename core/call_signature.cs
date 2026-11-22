using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	static class call_signature {
		public static bool returns_value(MethodSig signature) => signature.RetType.RemovePinnedAndModifiers().ElementType != ElementType.Void;
		public static int argument_count(MethodSig signature, bool constructor = false) => signature.Params.Count + (signature.ParamsAfterSentinel?.Count ?? 0) + (signature.ImplicitThis && !constructor ? 1 : 0);
		public static MethodSig @for(IMethod method) {
			var method_arguments = (method as MethodSpec)?.GenericInstMethodSig.GenericArguments;
			var type_arguments = (method.DeclaringType as TypeSpec)?.TypeSig is GenericInstSig instance ? instance.GenericArguments : null;
			return call_signature.instantiate(method.MethodSig, type_arguments, method_arguments);
		}
		public static MethodSig for_type(IMethod method, ITypeDefOrRef? declaring_type) => call_signature.instantiate(method.MethodSig,
			(declaring_type as TypeSpec)?.TypeSig is GenericInstSig instance ? instance.GenericArguments : null,
			(method as MethodSpec)?.GenericInstMethodSig.GenericArguments);
		public static TypeSig substitute(TypeSig type, System.Collections.Generic.IList<TypeSig>? type_arguments, int depth = 0) {
			if (depth > 48) throw new System.InvalidOperationException("generic signature nesting limit reached");
			if (type is GenericVar variable && type_arguments is not null && variable.Number < type_arguments.Count) return type_arguments[(int)variable.Number];
			return type switch {
				GenericInstSig instance => new GenericInstSig(instance.GenericType, instance.GenericArguments.Select(item => call_signature.substitute(item, type_arguments, depth + 1)).ToArray()),
				SZArraySig array => new SZArraySig(call_signature.substitute(array.Next, type_arguments, depth + 1)),
				ByRefSig reference => new ByRefSig(call_signature.substitute(reference.Next, type_arguments, depth + 1)),
				_ => type,
			};
		}
		static MethodSig instantiate(MethodSig original, System.Collections.Generic.IList<TypeSig>? type_arguments, System.Collections.Generic.IList<TypeSig>? method_arguments) {
			if (method_arguments is null && type_arguments is null) return original;
			TypeSig replace(TypeSig type, int depth = 0) {
				if (depth > 48) throw new System.InvalidOperationException("generic signature nesting limit reached");
				if (type is GenericMVar m && method_arguments is not null && m.Number < method_arguments.Count) return method_arguments[(int)m.Number];
				if (type is GenericVar t && type_arguments is not null && t.Number < type_arguments.Count) return type_arguments[(int)t.Number];
				return type switch {
					ByRefSig value => new ByRefSig(replace(value.Next, depth + 1)), PtrSig value => new PtrSig(replace(value.Next, depth + 1)), SZArraySig value => new SZArraySig(replace(value.Next, depth + 1)),
					ArraySig value => new ArraySig(replace(value.Next, depth + 1), value.Rank, value.Sizes, value.LowerBounds),
					GenericInstSig value => new GenericInstSig(value.GenericType, value.GenericArguments.Select(item => replace(item, depth + 1)).ToArray()),
					CModReqdSig value => new CModReqdSig(value.Modifier, replace(value.Next, depth + 1)), CModOptSig value => new CModOptSig(value.Modifier, replace(value.Next, depth + 1)),
					PinnedSig value => new PinnedSig(replace(value.Next, depth + 1)), _ => type,
				};
			}
			var signature = original.Clone(); signature.RetType = replace(signature.RetType);
			for (int index = 0; index < signature.Params.Count; index++) signature.Params[index] = replace(signature.Params[index]);
			if (signature.ParamsAfterSentinel is not null) for (int index = 0; index < signature.ParamsAfterSentinel.Count; index++) signature.ParamsAfterSentinel[index] = replace(signature.ParamsAfterSentinel[index]);
			return signature;
		}
		public static bool compatible(symbolic_value pointer, MethodDef method, MethodSig signature) {
			if (pointer.method_kind != method_value_kind.pointer) return false;
			var candidates = pointer.pointer_signatures.TryGetValue(method, out var values) ? values : new[] { method.MethodSig }.AsEnumerable();
			return candidates.Any(target => (target.CallingConvention & CallingConvention.Mask) == (signature.CallingConvention & CallingConvention.Mask) && target.HasThis == signature.HasThis && target.ExplicitThis == signature.ExplicitThis && target.Params.Count == signature.Params.Count && new SigComparer().Equals(target.RetType, signature.RetType) && target.Params.Zip(signature.Params, (a, b) => new SigComparer().Equals(a, b)).All(equal => equal) &&
				(target.IsVarArg || (target.ParamsAfterSentinel?.Count ?? 0) == (signature.ParamsAfterSentinel?.Count ?? 0) && (target.ParamsAfterSentinel is null || target.ParamsAfterSentinel.Zip(signature.ParamsAfterSentinel ?? new System.Collections.Generic.List<TypeSig>(), (a, b) => new SigComparer().Equals(a, b)).All(equal => equal))));
		}
	}
}
