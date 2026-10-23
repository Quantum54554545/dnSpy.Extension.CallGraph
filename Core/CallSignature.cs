using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	static class CallSignature {
		public static bool ReturnsValue(MethodSig signature) => signature.RetType.RemovePinnedAndModifiers().ElementType != ElementType.Void;
		public static int ArgumentCount(MethodSig signature, bool constructor = false) => signature.Params.Count + (signature.ParamsAfterSentinel?.Count ?? 0) + (signature.ImplicitThis && !constructor ? 1 : 0);
		public static MethodSig For(IMethod method) {
			var method_arguments = (method as MethodSpec)?.GenericInstMethodSig.GenericArguments;
			var type_arguments = (method.DeclaringType as TypeSpec)?.TypeSig is GenericInstSig instance ? instance.GenericArguments : null;
			if (method_arguments is null && type_arguments is null) return method.MethodSig;
			TypeSig Replace(TypeSig type, int depth = 0) {
				if (depth > 48) throw new System.InvalidOperationException("generic signature nesting limit reached");
				if (type is GenericMVar m && method_arguments is not null && m.Number < method_arguments.Count) return method_arguments[(int)m.Number];
				if (type is GenericVar t && type_arguments is not null && t.Number < type_arguments.Count) return type_arguments[(int)t.Number];
				return type switch {
					ByRefSig value => new ByRefSig(Replace(value.Next, depth + 1)), PtrSig value => new PtrSig(Replace(value.Next, depth + 1)), SZArraySig value => new SZArraySig(Replace(value.Next, depth + 1)),
					ArraySig value => new ArraySig(Replace(value.Next, depth + 1), value.Rank, value.Sizes, value.LowerBounds),
					GenericInstSig value => new GenericInstSig(value.GenericType, value.GenericArguments.Select(item => Replace(item, depth + 1)).ToArray()),
					CModReqdSig value => new CModReqdSig(value.Modifier, Replace(value.Next, depth + 1)), CModOptSig value => new CModOptSig(value.Modifier, Replace(value.Next, depth + 1)),
					PinnedSig value => new PinnedSig(Replace(value.Next, depth + 1)), _ => type,
				};
			}
			var signature = method.MethodSig.Clone(); signature.RetType = Replace(signature.RetType);
			for (int index = 0; index < signature.Params.Count; index++) signature.Params[index] = Replace(signature.Params[index]);
			if (signature.ParamsAfterSentinel is not null) for (int index = 0; index < signature.ParamsAfterSentinel.Count; index++) signature.ParamsAfterSentinel[index] = Replace(signature.ParamsAfterSentinel[index]);
			return signature;
		}
		public static bool Compatible(SymbolicValue pointer, MethodDef method, MethodSig signature) {
			if (pointer.MethodKind != MethodValueKind.Pointer) return false;
			var candidates = pointer.PointerSignatures.TryGetValue(method, out var values) ? values : new[] { method.MethodSig }.AsEnumerable();
			return candidates.Any(target => (target.CallingConvention & CallingConvention.Mask) == (signature.CallingConvention & CallingConvention.Mask) && target.HasThis == signature.HasThis && target.ExplicitThis == signature.ExplicitThis && target.Params.Count == signature.Params.Count && new SigComparer().Equals(target.RetType, signature.RetType) && target.Params.Zip(signature.Params, (a, b) => new SigComparer().Equals(a, b)).All(equal => equal) &&
				(target.IsVarArg || (target.ParamsAfterSentinel?.Count ?? 0) == (signature.ParamsAfterSentinel?.Count ?? 0) && (target.ParamsAfterSentinel is null || target.ParamsAfterSentinel.Zip(signature.ParamsAfterSentinel ?? new System.Collections.Generic.List<TypeSig>(), (a, b) => new SigComparer().Equals(a, b)).All(equal => equal))));
		}
	}
}
