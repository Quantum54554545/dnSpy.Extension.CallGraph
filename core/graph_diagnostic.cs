using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	enum graph_diagnostic_kind {
		address_reference,
		unresolved_pointer,
		unresolved_delegate,
		unresolved_reflection,
		unresolved_reference,
		analysis_budget,
		native_boundary,
		runtime_selection,
		unsupported_instruction,
		invalid_il,
		structural_call,
		view_depth,
		node_limit,
		filtered_target,
		assembly_boundary,
		workspace_boundary,
		refinement_progress,
	}
	sealed record graph_diagnostic(MethodDef method, uint offset, graph_diagnostic_kind kind, string detail, MethodDef? target = null);
}
