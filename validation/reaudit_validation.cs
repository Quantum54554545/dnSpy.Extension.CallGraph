using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using dnlib.DotNet;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;

namespace CallGraph.Validation {
	static partial class program {
		static void check_reaudit() {
			using var input = program.audit_module("Reaudit"); var root = program.audit_method(input, "Root"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second");
			program.audit_call(first, root); program.audit_call(second, root); program.audit_call(first, second);
			var workspace = new workspace_analysis(new[] { input }); var index = call_graph_index.build(input, default, workspace);
			foreach (var direction in new[] { call_graph_direction.callers, call_graph_direction.both }) {
				var slice = index.select(root, direction, 1, 120, workspace: workspace, structural: true); var model = new exploration_graph(root, direction, 120); model.merge(slice, true); var projected = model.project();
				program.check("visible depth-boundary cross-links survive exploration projection: " + direction, slice.links.Count == 3 && projected.links.Select(link => (link.source, link.target)).ToHashSet().SetEquals(slice.links.Select(link => (link.source, link.target))));
				model.collapse(second); if (direction == call_graph_direction.both) model.collapse(first);
				program.check("explicit collapse still suppresses the selected branch links: " + direction, !model.project().links.Any(link => link.source == first && link.target == second));
			}
			var large_sites = new[] { new call_site_info(root, 0, "calli", "", Enumerable.Repeat(first, 200001).ToArray(), Array.Empty<call_site_evidence>(), "unknown remainder") };
			var large_slice = new call_graph_slice(new[] { new call_graph_node(root, 0, true, false) }, Array.Empty<call_graph_link>(), false, 1, Array.Empty<graph_diagnostic>(), large_sites);
			var cache = new graph_selection_cache(); cache.store(new graph_selection_key(index, root, call_graph_direction.callers, 1, 120, "", false, null), 0, large_slice);
			program.check("direction snapshot limits account for retained call-site targets, not just visible cards", cache.count == 0 && large_slice.call_sites[0].targets.Count == 200001);
			var refined = workspace.refine_index(index, default, budget_override: new refinement_budget(step_limit: 0));
			using (var cancel = new CancellationTokenSource()) {
				cancel.Cancel(); bool honored = false; try { workspace.refine_index(index, cancel.Token, resume_refinement: false); } catch (OperationCanceledException) { honored = true; }
				program.check("cached refinement honors cancellation before returning its snapshot", honored);
			}
			program.check_map_incremental_layout(root, first, second);
		}
		static void check_map_incremental_layout(MethodDef root, MethodDef first, MethodDef second) {
			first.Rid = 10; second.Rid = 20;
			var map = new function_map_surface { Width = 1000, Height = 700 }; map.Measure(new Size(1000, 700)); map.Arrange(new Rect(0, 0, 1000, 700));
			call_graph_slice view(call_graph_direction direction, int layer, params MethodDef[] methods) => new call_graph_slice(new[] { new call_graph_node(root, 0, true, false) }.Concat(methods.Select(method => new call_graph_node(method, layer, false, false))).ToArray(), methods.Select(method => new call_graph_link(direction == call_graph_direction.callers ? method : root, direction == call_graph_direction.callers ? root : method, 1, new uint[] { 0 }, new[] { "call" })).ToArray(), false, 0, Array.Empty<graph_diagnostic>(), direction: direction);
			void render(call_graph_slice slice) { map.render(slice); program.pump_ui(() => !ReferenceEquals(program.ui_field(map, "slice"), slice)); }
			var initial = view(call_graph_direction.callees, 1, second); render(initial);
			var positions = (Dictionary<MethodDef, Point>)program.ui_field(map, "positions"); var original = positions[second];
			var expanded = view(call_graph_direction.callees, 1, first, second); render(expanded);
			program.check("map expansion retains an existing node without placing a lower-token new node on top of it", positions[second] == original && (positions[first] - positions[second]).Length >= 18);
			map.focus_method(second);
			var incoming = view(call_graph_direction.callers, -1, first, second); render(incoming);
			program.check("switching map direction moves shared callers to their incoming layer instead of keeping outgoing coordinates", positions[first].X < 0 && positions[second].X < 0);
			program.check("switching map direction cannot leave the viewport focused on obsolete outgoing coordinates", (Vector)program.ui_field(map, "pan") == new Vector());
			var changed_layer = view(call_graph_direction.callers, -2, first, second); render(changed_layer);
			program.check("a map node whose depth changes cannot retain a stale layer position", positions[first].X < -300 && positions[second].X < -300);
			var dense_methods = Enumerable.Range(0, 500).Select(position => program.audit_method(root.Module, "Dense" + position)).ToArray();
			var dense = new call_graph_slice(new[] { new call_graph_node(root, 0, true, false) }.Concat(dense_methods.Select((method, position) => new call_graph_node(method, position % 2 == 0 ? -1 : 1, false, false))).ToArray(), Array.Empty<call_graph_link>(), false);
			var packed = graph_layout.map(dense, default); var points = packed.Values.ToArray();
			program.check("dense map layers have finite nonoverlapping automatic node centers", packed.Count == 501 && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)) && points.SelectMany((point, position) => points.Skip(position + 1).Select(other => (other - point).Length)).All(distance => distance >= 23.999));
			program.check("wide map fan-in and fan-out stay on their own sides of the root", dense.nodes.Where(node => !node.is_root).All(node => Math.Sign(packed[node.method].X) == Math.Sign(node.layer)));
			using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); bool honored = false; try { graph_layout.map(dense, cancel.Token, packed); } catch (OperationCanceledException) { honored = true; } program.check("fully retained map layouts still honor cancellation", honored); }
			map.clear();
		}
	}
}
