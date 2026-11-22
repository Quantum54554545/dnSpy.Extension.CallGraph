using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_filter_policies() {
			using var input = program.audit_module("PolicyInput"); using var external = program.audit_module("PolicyExternal"); using var framework = program.audit_module("System.PolicyFixture"); using var runtime = ModuleDefMD.Load(typeof(object).Assembly.Location);
			framework.Assembly.PublicKey = runtime.Assembly.PublicKey;
			var root = program.audit_method(input, "Root");
			var regular = program.audit_method(input, "Regular"); var accessor = program.audit_method(input, "Accessor"); accessor.SemanticsAttributes = dnlib.DotNet.MethodSemanticsAttributes.Getter;
			var constructor = program.audit_method(input, "Constructor"); constructor.Name = ".ctor";
			var native = program.audit_method(input, "Native"); native.Body = null;
			var other_namespace = program.audit_method(input, "OtherNamespace"); other_namespace.DeclaringType.Namespace = "Other";
			var same_type = new MethodDefUser("SameType", root.MethodSig, root.ImplAttributes, root.Attributes) { Body = new CilBody() }; same_type.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); root.DeclaringType.Methods.Add(same_type);
			var outside = program.audit_method(external, "Outside"); var system = program.audit_method(framework, "SystemMethod");
			var targets = new[] { regular, accessor, constructor, native, other_namespace, same_type, outside, system };
			foreach (var target in targets) { program.audit_call(root, target); if (target.HasBody) program.audit_call(target, root); }
			var modules = new ModuleDef[] { input, external, framework }; var workspace = new workspace_analysis(modules);
			var index = call_graph_index.combine(modules.Select(module => call_graph_index.build(module, default, workspace)), workspace: workspace);
			program.check("policy fixture uses an authenticated framework identity", framework_identity.is_framework_type(system.DeclaringType));
			for (int mask = 0; mask < 128; mask++) {
				bool enabled(int bit) => (mask & (1 << bit)) != 0;
				var values = new Dictionary<string, object?> { ["HideSystemMethods"] = enabled(0), ["HideAccessors"] = enabled(1), ["HideConstructors"] = enabled(2), ["OnlyCurrentNamespace"] = enabled(3), ["OnlyCurrentType"] = enabled(4), ["RequireMethodBody"] = enabled(5), ["OnlyCurrentAssembly"] = enabled(6) };
				var filter = new call_graph_settings(depth_services.empty<ISettingsService>(values)).create_filter_options();
				bool allowed(MethodDef method) => !(enabled(0) && method == system || enabled(1) && method == accessor || enabled(2) && method == constructor || enabled(3) && method == other_namespace || enabled(4) && method != same_type || enabled(5) && method == native);
				foreach (var direction in new[] { call_graph_direction.callees, call_graph_direction.callers, call_graph_direction.both }) {
					var expected = targets.Where(allowed).Where(method => direction != call_graph_direction.callers || method.HasBody).Append(root).ToHashSet();
					var structural = index.select(root, direction, 3, 120, filter, workspace: workspace, structural: true);
					var forward = direction == call_graph_direction.callers ? null : workspace.browse(root, 120, filter, default, depth: 3, resume_refinement: false, budget_override: new refinement_budget(step_limit: 0)).slice;
					var final = direction == call_graph_direction.callees ? forward! : index.select(root, direction, 3, 120, filter, workspace: workspace, forward_slice: forward);
					var projection = new exploration_graph(root, direction, 120, filter); projection.merge(final, true); var displayed = projection.project();
					program.check($"saved filter combination {mask} applies to structural, final and projected {direction}", structural.nodes.Select(node => node.method).ToHashSet().SetEquals(expected) && final.nodes.Select(node => node.method).ToHashSet().SetEquals(expected) && displayed.nodes.Select(node => node.method).ToHashSet().SetEquals(expected));
				}
			}
			program.check("view filtering does not remove stored IL evidence", index.outgoing_value(root).Count == targets.Length && index.incoming_value(root).Count == targets.Length - 1);
			program.check_policy_boundaries();
			program.check_policy_ui();
		}
		static void check_policy_boundaries() {
			using var input = program.audit_module("PolicyBoundaries"); using var external = program.audit_module("ExternalBoundaries");
			var root = program.audit_method(input, "Root"); var caller = program.audit_method(input, "Caller"); var deeper = program.audit_method(input, "Deeper"); program.audit_call(caller, root); program.audit_call(caller, deeper);
			var workspace = new workspace_analysis(new[] { input }); var index = call_graph_index.build(input, default, workspace);
			var slice = index.select(root, call_graph_direction.both, 1, 120, workspace: workspace, structural: true);
			var model = new exploration_graph(root, call_graph_direction.both, 120); model.merge(slice, true);
			program.check("combined view projection cannot automatically expand an incoming depth-boundary card", model.project().nodes.Select(node => node.method).ToHashSet().SetEquals(new[] { root, caller }));
			var outside = program.audit_method(external, "Outside"); var outside_child = program.audit_method(external, "OutsideChild"); program.audit_call(root, outside); program.audit_call(outside, root); program.audit_call(outside, outside_child);
			workspace = new workspace_analysis(new ModuleDef[] { input, external }); index = call_graph_index.combine(new[] { call_graph_index.build(input, default, workspace), call_graph_index.build(external, default, workspace) }, workspace: workspace);
			var filter = new call_graph_filter_options(only_current_assembly: true); slice = index.select(root, call_graph_direction.both, 3, 120, filter, workspace: workspace, structural: true); model = new exploration_graph(root, call_graph_direction.both, 120, filter); model.merge(slice, true);
			program.check("assembly-boundary leaves remain unexpanded even with a visible back edge", !model.project().nodes.Any(node => node.method == outside_child));
			var busy = program.audit_method(input, "Busy"); foreach (int position in Enumerable.Range(0, 8)) program.audit_call(busy, program.audit_method(input, "Child" + position)); program.audit_call(outside, busy);
			workspace = new workspace_analysis(new ModuleDef[] { input, external }); index = call_graph_index.combine(new[] { call_graph_index.build(input, default, workspace), call_graph_index.build(external, default, workspace) }, workspace: workspace);
			var bounded = index.select(busy, call_graph_direction.both, 1, 4, filter, workspace: workspace, structural: true);
			program.check("combined view reserves room for allowed external caller leaves under assembly policy", bounded.nodes.Any(node => node.method == outside) && bounded.nodes.Count == 4);
		}
		static void check_policy_ui() {
			using var input = program.audit_module("PolicyUi"); var root = program.audit_method(input, "Root"); var accessor = program.audit_method(input, "Accessor"); accessor.SemanticsAttributes = dnlib.DotNet.MethodSemanticsAttributes.Getter; program.audit_call(root, accessor); program.audit_call(accessor, root);
			var regular = program.audit_method(input, "Regular"); program.audit_call(root, regular); program.audit_call(regular, root);
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); var cache = new call_graph_index_cache(); string path = Path.Combine(Path.GetTempPath(), "CallGraph-policy-" + Guid.NewGuid().ToString("N") + ".json");
			var decompiler = depth_services.empty<IDecompiler>(new Dictionary<string, object?> { ["$get_GenericGuid"] = DecompilerConstants.LANGUAGE_CSHARP });
			var decompilers = depth_services.empty<IDecompilerService>(new Dictionary<string, object?> { ["$get_AllDecompilers"] = new[] { decompiler } });
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), decompilers, settings, depth_services.empty<IThemeService>(), cache, new research_store(path));
			var blocks = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(blocks, "theme_service", null!); var map = (function_map_surface)control.FindName("function_map_surface");
			var direction = (ComboBox)control.FindName("direction_combo_box"); var view = (ComboBox)control.FindName("view_mode_combo_box");
			try {
				for (int mode = 0; mode < 2; mode++) for (int selected = 0; selected < 3; selected++) {
					view.SelectedIndex = mode; direction.SelectedIndex = selected;
					foreach (bool hide in new[] { false, true, false }) {
						program.apply_options(settings, accessors: hide); control.show_method(root);
						program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || (int)program.ui_field(control, "view_workers") != 0 || blocks.is_rendering);
						var expected = hide ? new[] { root, regular } : new[] { root, regular, accessor };
						program.pump_ui(() => mode == 1 && ((Dictionary<MethodDef, Point>)program.ui_field(map, "positions")).Count != expected.Length);
						var drawn = mode == 0 ? ((Dictionary<object, graph_node_presentation>)program.ui_field(blocks, "node_presentations")).Keys.OfType<MethodDef>() : ((Dictionary<MethodDef, Point>)program.ui_field(map, "positions")).Keys;
						program.check($"WPF view {mode}, direction {selected} applies and reverses saved accessor policy {hide}", drawn.ToHashSet().SetEquals(expected));
					}
				}
			} finally { control.dispose_graph(); if (File.Exists(path)) File.Delete(path); }
		}
	}
}
