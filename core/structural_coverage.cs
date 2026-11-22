using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed record coverage_issue(MethodDef method, uint offset, string detail);
	static class structural_coverage {
		public static coverage_issue[] check(workspace_analysis workspace, call_graph_slice slice, CancellationToken token) {
			var issues = new List<coverage_issue>(); var visible = slice.nodes.Select(node => node.method).ToHashSet();
			var sites = slice.call_sites.ToLookup(site => (source: site.source, offset: site.offset));
			var edges = slice.links.SelectMany(link => link.il_offsets.Select(offset => (source: link.source, target: link.target, offset: offset))).ToHashSet();
			foreach (var node in slice.nodes.Where(node => node.is_expanded)) {
				foreach (var instruction in workspace.call_structure(node.method, token).calls) {
					token.ThrowIfCancellationRequested(); var found = sites[(source: node.method, offset: instruction.Offset)].ToArray();
					if (found.Length == 0) { issues.Add(new coverage_issue(node.method, instruction.Offset, "IL call site missing from model")); continue; }
					if (instruction.Operand is not IMethod reference || workspace.resolve(reference) is not MethodDef target) continue;
					if (!found.Any(site => site.targets.Contains(target))) { issues.Add(new coverage_issue(node.method, instruction.Offset, "Declared IL target missing from model")); continue; }
					bool edge = edges.Contains((source: node.method, target: target, offset: instruction.Offset));
					bool boundary = slice.diagnostics_for(node.method).Any(note => note.kind is graph_diagnostic_kind.node_limit or graph_diagnostic_kind.filtered_target or graph_diagnostic_kind.view_depth or graph_diagnostic_kind.assembly_boundary or graph_diagnostic_kind.workspace_boundary);
					if (!edge && (visible.Contains(target) && !slice.diagnostics_for(node.method).Any(note => note.kind == graph_diagnostic_kind.filtered_target) || !boundary)) issues.Add(new coverage_issue(node.method, instruction.Offset, "IL target has neither a visible edge nor a view boundary"));
				}
			}
			return issues.ToArray();
		}
	}
}
