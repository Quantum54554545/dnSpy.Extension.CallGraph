using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;

namespace CallGraph.Validation {
	static partial class program {
		
		static ModuleDefUser audit_module(string name) {
			var result = new ModuleDefUser(name); new AssemblyDefUser(name, new Version(1, 0)).Modules.Add(result); return result;
		}
		static MethodDef audit_method(ModuleDef module, string type_name) {
			var owner = new TypeDefUser("Audit", type_name, module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(owner);
			var method = new MethodDefUser("run", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
				dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
			owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
		}
		static void audit_call(MethodDef source, IMethod target) {
			source.Body.Instructions.Insert(source.Body.Instructions.Count - 1, Instruction.Create(OpCodes.Call, target)); source.Body.UpdateInstructionOffsets();
		}
		static void check_audit_regressions() {
			using var first = program.audit_module("AuditFirst"); using var second = program.audit_module("AuditSecond");
			var root = program.audit_method(first, "Root"); var cycle = program.audit_method(first, "Cycle"); var ancestor = program.audit_method(first, "Ancestor");
			program.audit_call(root, cycle); program.audit_call(cycle, root); program.audit_call(ancestor, cycle);
			var workspace = new workspace_analysis(new[] { first }); var index = call_graph_index.build(first, default, workspace);
			var both = index.select(root, call_graph_direction.both, 8, 3, call_graph_filter_options.none, default, workspace, true);
			program.check("both directions retain shared cycle at node limit", both.nodes.Any(node => node.method == ancestor));
			program.check("both directions do not duplicate cycle edges", both.links.Select(link => (source: link.source, target: link.target)).Distinct().Count() == both.links.Count);
			program.check("both directions preserve forward layers", both.nodes.Single(node => node.method == cycle).layer > 0);
			var target = program.audit_method(second, "Target");
			var declaration = new TypeRefUser(first, target.DeclaringType.Namespace, target.DeclaringType.Name, new AssemblyRefUser(second.Assembly));
			var reference = new MemberRefUser(first, target.Name, target.MethodSig, declaration);
			program.audit_call(root, reference);
			var cache = new call_graph_index_cache(); var old_workspace = cache.get_workspace(new[] { first });
			var stale = call_graph_index.build(first, default, old_workspace); cache.store(stale);
			program.check("unloaded assembly reference starts unresolved", !stale.outgoing_value(root).Any(link => link.target == target));
			var loaded = cache.get_workspace(new ModuleDef[] { first, second });
			program.check("loading assembly invalidates resolution-dependent indexes", !cache.try_get_value(first, out _));
			var rebuilt = call_graph_index.build(first, default, loaded);
			program.check("reloaded index resolves newly loaded assembly", rebuilt.outgoing_value(root).Any(link => link.target == target));
			cache.store(rebuilt); cache.invalidate(second);
			program.check("callee edit invalidates caller index", !cache.try_get_value(first, out _));
			var after_edit = cache.get_workspace(new ModuleDef[] { first, second });
			program.check("invalidation advances workspace generation", !ReferenceEquals(loaded, after_edit));
			program.check("stale worker cannot repopulate cache after edit", !cache.store(rebuilt, loaded) && !cache.try_get_value(first, out _));
			cache.store(call_graph_index.build(first, default, after_edit), after_edit);
			cache.remove_missing(new HashSet<ModuleDef> { first });
			program.check("unloading assembly invalidates caller references", !cache.try_get_value(first, out _));
			var budgeted = new reachable_call_analysis(new ModuleDef[] { first, second }, default) { budget = new refinement_budget(allocation_limit: 0) };
			var limited = budgeted.select(root, 8, 10, call_graph_filter_options.none);
			program.check("refinement resource limit preserves structural calls", limited.is_truncated && limited.links.Any(link => link.source == root && link.target == cycle) && limited.links.Any(link => link.source == root && link.target == target));
			program.check("refinement resource limit explains missing dynamic precision", limited.diagnostics.Any(note => note.kind == graph_diagnostic_kind.analysis_budget && note.detail.Contains("allocation budget", StringComparison.Ordinal)));
			var policy_workspace = new workspace_analysis(new[] { first });
			var fast = policy_workspace.analyze(root, 2, 10, call_graph_filter_options.none, null, default).slice;
			var deeper = policy_workspace.analyze(root, 2, 10, new call_graph_filter_options(thorough_refinement: true), null, default).slice;
			program.check("fast and deeper refinement have independent query caches", policy_workspace.analysis_count == 2 && !ReferenceEquals(fast, deeper));
			program.check_ui_audit();
		}
		static object ui_field(object surface, string name) => surface.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
		static void set_ui_field(object surface, string name, object value) => surface.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, value);
		static void pump_ui(Func<bool> pending) {
			var clock = Stopwatch.StartNew();
			while (clock.ElapsedMilliseconds < 3000 && pending()) {
				var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
				Dispatcher.PushFrame(frame); Thread.Sleep(1);
			}
		}
		static void check_ui_audit() {
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
			var surface = new call_graph_surface();
			var rectangles = (Dictionary<object, Rect>)program.ui_field(surface, "node_rects");
			var presentations = (Dictionary<object, graph_node_presentation>)program.ui_field(surface, "node_presentations");
			program.set_ui_field(surface, "vertical_layout", true);
			foreach (var entry in new[] { (key: "left", layer: 0, rect: new Rect(0, 0, 100, 100)), (key: "right", layer: 0, rect: new Rect(130, 0, 100, 150)),
				(key: "lower-left", layer: 1, rect: new Rect(30, 200, 100, 100)), (key: "lower-right", layer: 1, rect: new Rect(160, 200, 100, 100)) }) {
				rectangles[entry.key] = entry.rect;
				presentations[entry.key] = new graph_node_presentation(entry.key, entry.layer, false, false, entry.key, string.Empty, string.Empty, string.Empty);
			}
			var resize = typeof(call_graph_surface).GetMethod("resize_node", BindingFlags.Instance | BindingFlags.NonPublic)!;
			resize.Invoke(surface, new object[] { "left", 300.0, new HashSet<object>() });
			program.check("vertical resize moves entire following row across columns", rectangles["lower-left"].Top == 350 && rectangles["lower-right"].Top == 350 && rectangles["right"].Top == 0);
			resize.Invoke(surface, new object[] { "right", 200.0, new HashSet<object>() });
			program.check("shorter vertical sibling does not shift following rows", rectangles["lower-left"].Top == 350);
			surface.clear();
			call_graph_slice single(MethodDef method) => new call_graph_slice(new[] { new call_graph_node(method, 0, true, false) }, Array.Empty<call_graph_link>(), false);
			var old_graph = single(program.fixture("leaf")); var next_graph = single(program.fixture("other_leaf"));
			IReadOnlyDictionary<MethodDef, highlighted_snippet> snippets(call_graph_slice slice) => slice.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("call();"));
			surface.render(old_graph, snippets(old_graph));
			program.set_ui_field(surface, "is_pan_candidate", true); int version = (int)program.ui_field(surface, "render_version");
			surface.render(next_graph, snippets(next_graph));
			program.check("deferred card render invalidates older layout immediately", (int)program.ui_field(surface, "render_version") > version);
			program.pump_ui(() => surface.is_rendering);
			program.set_ui_field(surface, "is_pan_candidate", false); ((Action)program.ui_field(surface, "pending_render"))();
			program.pump_ui(() => surface.is_rendering);
			program.check("dragging cannot restore stale card graph", surface.is_showing(program.fixture("other_leaf")));
			var map = new function_map_surface(); map.render(old_graph);
			program.set_ui_field(map, "drag_candidate", true); version = (int)program.ui_field(map, "render_version"); map.render(next_graph);
			program.check("deferred map render invalidates older layout immediately", (int)program.ui_field(map, "render_version") > version);
			program.set_ui_field(map, "drag_candidate", false); ((Action)program.ui_field(map, "pending_render"))();
			program.pump_ui(() => !map.is_showing(program.fixture("other_leaf")));
			program.check("dragging cannot restore stale function map", map.is_showing(program.fixture("other_leaf")));
			surface.clear(); map.clear();
		}
	}
}
