using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	enum GraphDiagnosticKind {
		AddressReference,
		UnresolvedPointer,
		UnresolvedDelegate,
		UnresolvedReflection,
		UnresolvedReference,
		AnalysisBudget,
		NativeBoundary,
		RuntimeSelection,
		UnsupportedInstruction,
		InvalidIl,
	}
	sealed record GraphDiagnostic(MethodDef Method, uint Offset, GraphDiagnosticKind Kind, string Detail, MethodDef? Target = null);
}
