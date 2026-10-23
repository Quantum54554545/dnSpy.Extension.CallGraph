using System;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	static class FrameworkIdentity {
		public static bool IsFrameworkType(ITypeDefOrRef? type) {
			if (type is TypeSpec specification && specification.TypeSig is GenericInstSig instance) type = instance.GenericType.TypeDefOrRef;
			var assembly = type?.DefinitionAssembly;
			string? name = assembly?.Name;
			if (name is null || !(name == "mscorlib" || name == "netstandard" || name == "Microsoft.CSharp" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal))) return false;
			string? token = PublicKeyBase.ToPublicKeyToken(assembly?.PublicKeyOrToken)?.ToString();
			return token is "b77a5c561934e089" or "b03f5f7f11d50a3a" or "7cec85d7bea7798e" or "cc7b13ffcd2ddd51";
		}
	}
}
