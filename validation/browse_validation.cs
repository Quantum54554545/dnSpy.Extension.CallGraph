using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;

namespace CallGraph.Validation {
	static partial class program {
		static void check_browse() {
			var guarded = new workspace_analysis(new[] { program.module }).browse(program.fixture("known_branch"), 120, call_graph_filter_options.none, default).slice;
			program.check("browser preserves both branches even with a constant argument", program.has(guarded, program.fixture("leaf")) && program.has(guarded, program.fixture("other_leaf")));
			using var input = program.audit_module("Browse"); using var external = program.audit_module("External");
			var root = program.audit_method(input, "Switch");
			root.Body.Instructions.Clear(); root.MethodSig.Params.Add(input.CorLibTypes.Int32);
			var branches = Enumerable.Range(0, 8).Select(index => program.audit_method(input, "branch" + index)).ToArray();
			var labels = branches.Select(method => Instruction.Create(OpCodes.Call, method)).ToArray();
			root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Switch, labels));
			root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			foreach (var label in labels) { root.Body.Instructions.Add(label); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
			root.Body.UpdateInstructionOffsets();
			var third = program.audit_method(input, "Third"); var fourth = program.audit_method(input, "Fourth"); program.audit_call(branches[0], third); program.audit_call(third, fourth);
			var workspace = new workspace_analysis(new ModuleDef[] { input, external });
			var slice = workspace.browse(root, 120, call_graph_filter_options.none, default).slice;
			program.check("browser displays all eight switch calls, not one modeled branch", branches.All(method => slice.links.Any(link => link.source == root && link.target == method)));
			program.check("browser refines without replacing structural switch calls", workspace.analysis_count == 1 && !slice.is_structural && branches.All(method => program.has(slice, method)));
			program.check("fixed depth has an explicit continuation hint", slice.nodes.Single(node => node.method == third).expansion_hint.Length != 0 && !slice.nodes.Single(node => node.method == third).is_expanded && !program.has(slice, fourth));
			program.check("explore from boundary continues to next callees", program.has(workspace.browse(third, 120, call_graph_filter_options.none, default).slice, fourth));
			var limited = workspace.browse(root, 2, call_graph_filter_options.none, default).slice;
			program.check("node budget does not silently pretend all targets are shown", limited.nodes.Single(node => node.method == root).expansion_hint.Length != 0);
			var outside = program.audit_method(external, "Outside"); var deeper = program.audit_method(external, "ExternalChild"); program.audit_call(outside, deeper); program.audit_call(root, outside);
			var scoped = new workspace_analysis(new ModuleDef[] { input, external }).browse(root, 120, new call_graph_filter_options(only_current_assembly: true), default).slice;
			program.check("assembly scope shows external calls as leaves instead of hiding them", program.has(scoped, outside) && !program.has(scoped, deeper));
			var missing = new MemberRefUser(input, "Missing", MethodSig.CreateStatic(input.CorLibTypes.Void), new TypeRefUser(input, "Unavailable", "API", new AssemblyRefUser("Unavailable", new Version(1, 0))));
			program.audit_call(root, missing);
			var unknown = new workspace_analysis(new[] { input }).browse(root, 120, call_graph_filter_options.none, default).slice;
			var view = call_graph_presentation.build(unknown, unknown.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("call();")));
			program.check("unresolved metadata produces a visible target card and edge", view.graph.nodes.Any(node => node.reference is unresolved_call_target && node.title.Contains("Missing", StringComparison.Ordinal)) && view.graph.links.Any(link => link.source == root && link.target is unresolved_call_target));
			var pointer = new workspace_analysis(new[] { program.module }).browse(program.fixture("unknown_pointer"), 120, call_graph_filter_options.none, default).slice;
			var pointer_view = call_graph_presentation.build(pointer, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("unresolved calli has its own card rather than a missing edge", pointer_view.graph.links.Any(link => link.source == program.fixture("unknown_pointer") && link.target is unresolved_call_target { operation: "calli" }));
			var surface = new call_graph_surface(); surface.render(slice, slice.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("call();")));
			program.pump_ui(() => surface.is_rendering);
			var edges = ((IEnumerable)program.ui_field(surface, "edge_visuals")).Cast<object>().ToArray();
			var starts = edges.Where(edge => ((graph_link_presentation)edge.GetType().GetProperty("link")!.GetValue(edge)!).source == root)
				.Select(edge => PathGeometry.CreateFromGeometry((Geometry)edge.GetType().GetProperty("curve")!.GetValue(edge)!).Figures[0].StartPoint).ToArray();
			program.check("eight sibling links have distinct visible source ports", starts.Length == 8 && starts.Distinct().Count() == 8);
			program.check("rendered edge count agrees with graph data", edges.Length == slice.links.Count);
			surface.clear();
		}

	}
}
