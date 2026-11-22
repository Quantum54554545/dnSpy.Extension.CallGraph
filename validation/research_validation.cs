using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_research() {
			using var input = program.audit_module("Research"); var root = program.audit_method(input, "Root"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second"); var shared = program.audit_method(input, "Shared"); var exclusive = program.audit_method(input, "Exclusive"); var tail = program.audit_method(input, "Tail");
			program.audit_call(root, first); program.audit_call(root, second); program.audit_call(first, shared); program.audit_call(first, exclusive); program.audit_call(second, shared); program.audit_call(shared, tail);
			var workspace = new workspace_analysis(new[] { input });
			var initial = workspace.structural_slice(root, 1, 120, call_graph_filter_options.none, default);
			var graph = new exploration_graph(root, call_graph_direction.callees, 120); graph.merge(initial, true);
			program.check("exploration begins at the selected depth", graph.project().nodes.Count == 3);
			graph.merge(workspace.structural_slice(first, 1, int.MaxValue, call_graph_filter_options.none, default)); graph.expand(first); var expanded = graph.project();
			program.check("expand adds neighbors without changing the root", expanded.nodes.Single(node => node.is_root).method == root && expanded.nodes.Any(node => node.method == exclusive));
			graph.merge(workspace.structural_slice(second, 1, int.MaxValue, call_graph_filter_options.none, default)); graph.expand(second); graph.collapse(first); var collapsed = graph.project();
			program.check("collapse removes only the exclusive branch", !collapsed.nodes.Any(node => node.method == exclusive) && collapsed.nodes.Any(node => node.method == shared));
			program.check("collapse keeps a shared target reachable from another parent", collapsed.links.Any(link => link.source == second && link.target == shared));
			graph.expand(first); graph.pin(exclusive); graph.collapse(first); program.check("pinned node survives branch collapse", graph.project().nodes.Any(node => node.method == exclusive));
			graph.pin(exclusive); program.check("unpin allows an unreachable node to disappear", !graph.project().nodes.Any(node => node.method == exclusive));
			graph.expand(first); program.check("reopening a branch reuses retained model", graph.project().nodes.Any(node => node.method == exclusive));
			var small = new exploration_graph(root, call_graph_direction.callees, 3); small.merge(workspace.structural_slice(root, 2, 3, call_graph_filter_options.none, default), true);
			var limited = small.project(); program.check("model retains targets outside the display budget", small.known_method_count > limited.nodes.Count && limited.nodes.Count == 3);
			program.check("limited projection explains continuation", limited.diagnostics.Any(note => note.kind == graph_diagnostic_kind.node_limit));
			small.pin(first); small.collapse(root); small.expand(first); var resumed = small.project(); program.check("collapsing while keeping a pinned branch reveals retained calls", resumed.nodes.Count == 3 && resumed.nodes.Any(node => node.method == exclusive || node.method == shared));
			program.check("structural coverage accepts explicit view boundaries", structural_coverage.check(workspace, limited, default).Length == 0);
			var broken = new call_graph_slice(initial.nodes, Array.Empty<call_graph_link>(), false, 0, initial.diagnostics, initial.call_sites);
			program.check("structural coverage detects a dropped visible IL edge", structural_coverage.check(workspace, broken, default).Any(issue => issue.method == root));
			var missing_site = new call_graph_slice(initial.nodes, initial.links, false, 0, initial.diagnostics, Array.Empty<call_site_info>());
			program.check("structural coverage detects lost call-site data", structural_coverage.check(workspace, missing_site, default).Length == 2);
			program.check("expanded graph preserves all structural sites", structural_coverage.check(workspace, expanded, default).Length == 0);
			var copy = graph.copy(); copy.collapse(first); program.check("background projection copies do not mutate the live exploration", graph.project().nodes.Any(node => node.method == exclusive) && !copy.project().nodes.Any(node => node.method == exclusive));
			var index = call_graph_index.build(input, default, workspace); var incoming = new exploration_graph(shared, call_graph_direction.callers, 120); incoming.merge(index.select(shared, call_graph_direction.callers, 1, 120, call_graph_filter_options.none, default, workspace, true), true);
			program.check("caller exploration preserves incoming direction", incoming.project().links.Count == 2 && incoming.project().nodes.Single(node => node.is_root).method == shared);
			incoming.merge(index.select(first, call_graph_direction.callers, 1, 120, call_graph_filter_options.none, default, workspace, true)); incoming.expand(first);
			program.check("caller expansion adds ancestors without switching root", incoming.project().nodes.Any(node => node.method == root) && incoming.root == shared);
			var filtered = new exploration_graph(root, call_graph_direction.callees, 120, new call_graph_filter_options(only_current_type: true)); filtered.merge(initial, true); program.check("exploration does not bypass explicit type filters", filtered.project().nodes.Count == 1);
			using var external = program.audit_module("ResearchExternal"); var outside = program.audit_method(external, "Outside"); program.audit_call(root, outside);
			var cross_workspace = new workspace_analysis(new ModuleDef[] { input, external }); var cross = new exploration_graph(root, call_graph_direction.callees, 120, new call_graph_filter_options(only_current_assembly: true)); cross.merge(cross_workspace.structural_slice(root, 1, 120, new call_graph_filter_options(only_current_assembly: true), default), true);
			program.check("assembly scope retains external leaf operands", cross.project().nodes.Any(node => node.method == outside));
			var search = new workspace_search(workspace, default);
			program.check("workspace method search reaches non-visible methods", search.find(workspace_search_kind.methods, "Tail", default).Any(hit => hit.method == tail));
			program.check("workspace caller search finds all declared direct sites", search.callers(shared).Count == 2);
			var path = search.path(root, tail, default); program.check("workspace finds a shortest direct IL chain", path is { Length: 3 } && path[0].method == root && path[^1].operand == tail);
			program.check("workspace path search terminates without inventing a reverse path", search.path(tail, root, default) is null);
			using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool observed = false; try { search.find(workspace_search_kind.methods, "Root", cancelled.Token); } catch (OperationCanceledException) { observed = true; } program.check("workspace search observes cancellation", observed);
			var long_method = program.audit_method(input, "LongBody"); long_method.Body.Instructions.Clear(); for (int position = 0; position < 200001; position++) long_method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop)); long_method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, tail)); long_method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); long_method.Body.UpdateInstructionOffsets();
			var full_structure = new workspace_analysis(new[] { input }).structure(long_method, default); program.check("structural scan does not silently discard calls after 200000 instructions", !full_structure.is_incomplete && full_structure.calls.Single().Operand == tail);
			string store_path = Path.Combine(Path.GetTempPath(), "CallGraph-research-" + Guid.NewGuid().ToString("N"), "research.json"); var store = new research_store(store_path);
			store.set(first, new research_annotation("Load payload", "Check buffer ownership", "Loader", true)); store.set(second, new research_annotation(group: "Loader"));
			store.set_positions(root, new Dictionary<string, research_position> { [research_store.key(first)] = new(150, 240) }); program.check("research saves atomically", store.save()); var restored = new research_store(store_path);
			program.check("aliases notes groups and compact state round-trip", restored.get(first) == store.get(first)); program.check("positions round-trip separately from the assembly", restored.positions(root)[research_store.key(first)] == new research_position(150, 240));
			var view = call_graph_presentation.build(initial, initial.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("Original code")));
			var grouped = research_presentation.apply(view, store, new HashSet<string>(), _ => false);
			program.check("group projection preserves external links without mutating the IL model", grouped.graph.nodes.Count == 2 && grouped.graph.links.Single().call_site_count == 2 && initial.links.Count == 2);
			var ungrouped = research_presentation.apply(view, store, new HashSet<string> { "Loader" }, _ => false);
			program.check("group expansion restores original method nodes and links", ungrouped.graph.nodes.Count == 3 && ungrouped.graph.links.Count == 2);
			program.check("alias and note are visible without metadata mutation", ungrouped.graph.nodes.Single(node => node.reference == first).title == "Load payload" && first.Name == "run" && ungrouped.graph.nodes.Single(node => node.reference == first).tool_tip.Contains("Check buffer ownership", StringComparison.Ordinal));
			program.check("compact card is optional and does not replace stored code", ungrouped.snippets[first].text.Length == 0 && view.snippets[first].text == "Original code");
			var grouped_budget = new exploration_graph(root, call_graph_direction.callees, 2); grouped_budget.merge(initial, true); var grouped_slice = grouped_budget.project(method => method == first || method == second ? (object)new research_group("Loader") : method);
			program.check("collapsed group consumes one display slot, not one per method", grouped_slice.nodes.Count == 3 && research_presentation.apply(call_graph_presentation.build(grouped_slice, new Dictionary<MethodDef, highlighted_snippet>()), store, new HashSet<string>(), _ => false).graph.nodes.Count == 2);
			store.remove_group("Loader"); program.check("removing a group restores all its members", store.get(first).group.Length == 0 && store.get(second).group.Length == 0);
			File.WriteAllText(store_path, "broken json"); var damaged = new research_store(store_path); damaged.set(first, new research_annotation("Do not overwrite")); program.check("corrupt research file is not silently overwritten", !damaged.save() && File.ReadAllText(store_path) == "broken json");
			program.check_value_sources(); program.check_research_layout(initial, expanded, store_path); program.check_research_controller(root, first, exclusive, store_path);
			File.Delete(store_path); Directory.Delete(Path.GetDirectoryName(store_path)!);
		}
		static void check_value_sources() {
			using var input = program.audit_module("value_sources"); var target = program.audit_method(input, "Target"); var setter = program.audit_method(input, "initializer"); setter.Name = ".cctor"; var consumer = program.audit_method(input, "Consumer");
			var field = new FieldDefUser("Address", new FieldSig(input.CorLibTypes.IntPtr), dnlib.DotNet.FieldAttributes.Static | dnlib.DotNet.FieldAttributes.Assembly); setter.DeclaringType.Fields.Add(field);
			setter.Body.Instructions.Clear(); setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, target)); setter.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field)); setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); setter.Body.UpdateInstructionOffsets();
			consumer.Body.Instructions.Clear(); consumer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field)); consumer.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void))); consumer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "payload-key")); consumer.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); consumer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); consumer.Body.UpdateInstructionOffsets();
			var workspace = new workspace_analysis(new[] { input }); var search = new workspace_search(workspace, default); var sources = new value_sources(workspace, search);
			program.check("workspace strings retain exact source offsets", search.find(workspace_search_kind.strings, "payload-key", default).Single() is var hit && hit.method == consumer && hit.offset == consumer.Body.Instructions[2].Offset);
			program.check("workspace address references are separate from calls", search.find(workspace_search_kind.addresses, "Target", default).Single().method == setter && search.callers(target).Count == 0);
			program.check("workspace field writes include type initializers", search.writers_value(field).Single().method == setter && search.find(workspace_search_kind.field_writes, "Address", default).Length == 1 && search.find(workspace_search_kind.field_reads, "Address", default).Length == 1);
			program.check("calli address is linked to its actual stack producer", sources.parents(new value_origin(consumer, 1), default).Any(parent => parent.source == new value_origin(consumer, 0)));
			program.check("address source is labeled separately from call arguments", sources.parents(new value_origin(consumer, 1), default).Any(parent => parent.label == "fnptr"));
			var writes = sources.parents(new value_origin(consumer, 0), default); program.check("field source reveals potential writes, without claiming execution order", writes.Any(parent => parent.source == new value_origin(setter, 1)) && writes.Any(parent => parent.source.position < 0));
			program.check("field write reveals ldftn address origin", sources.parents(new value_origin(setter, 1), default).Any(parent => parent.source == new value_origin(setter, 0)));
			var source_window = new value_sources_window(consumer, workspace, search, null!, _ => { });
			var source_surface = (call_graph_surface)program.ui_field(source_window, "surface"); var source_panel = (FrameworkElement)source_window.Content; source_panel.Measure(new Size(1000, 600)); source_panel.Arrange(new Rect(0, 0, 1000, 600));
			((ComboBox)program.ui_field(source_window, "sites")).SelectedIndex = 0;
			program.pump_ui(() => ((Dictionary<value_origin, value_dependency[]>)program.ui_field(source_window, "parents")).Count == 0 || source_surface.is_rendering);
			var source_nodes = (Dictionary<object, Rect>)program.ui_field(source_surface, "node_rects"); program.check("value source window renders the real calli producer graph", source_nodes.ContainsKey(new value_origin(consumer, 1)) && source_nodes.ContainsKey(new value_origin(consumer, 0)));
			var expansion = (System.Threading.Tasks.Task)typeof(value_sources_window).GetMethod("expand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source_window, new object[] { new value_origin(consumer, 0) })!;
			program.pump_ui(() => !expansion.IsCompleted || source_surface.is_rendering); expansion.GetAwaiter().GetResult();
			program.check("value source window expands cross-method writes without losing its consumer", source_nodes.ContainsKey(new value_origin(setter, 1)) && source_nodes.ContainsKey(new value_origin(consumer, 1))); source_window.Close();
			var branching = program.audit_method(input, "BranchValues"); branching.MethodSig.Params.Add(input.CorLibTypes.Int32); branching.Body.Variables.Add(new Local(input.CorLibTypes.Int32)); branching.Body.Instructions.Clear();
			var alternate = Instruction.Create(OpCodes.Ldc_I4_2); var load = Instruction.Create(OpCodes.Ldloc_0); var value_target = program.audit_method(input, "ArgumentTarget"); value_target.MethodSig.Params.Add(input.CorLibTypes.Int32); value_target.Body.Instructions.Clear(); value_target.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); value_target.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); value_target.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brtrue, alternate), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Br, load), alternate, Instruction.Create(OpCodes.Stloc_0), load, Instruction.Create(OpCodes.Call, value_target), Instruction.Create(OpCodes.Ret) }) branching.Body.Instructions.Add(instruction);
			branching.Body.UpdateInstructionOffsets(); var flow = new method_value_sources(branching, default);
			program.check("value sources merge both branch assignments instead of picking one", flow.dependencies[7].SetEquals(new[] { 3, 6 }) && !flow.incomplete);
			var loop = program.audit_method(input, "LoopValues"); loop.Body.Instructions.Clear(); loop.Body.Variables.Add(new Local(input.CorLibTypes.Int32)); var loop_load = Instruction.Create(OpCodes.Ldloc_0); foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc_0), loop_load, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Br, loop_load) }) loop.Body.Instructions.Add(instruction); loop.Body.UpdateInstructionOffsets();
			var loop_flow = new method_value_sources(loop, default); program.check("value dependency fixed point terminates on a mutation loop", loop_flow.dependencies[2].SetEquals(new[] { 1, 5 }) && !loop_flow.incomplete);
			var updated_search = new workspace_search(new workspace_analysis(new[] { input }), default); var updated_sources = new value_sources(new workspace_analysis(new[] { input }), updated_search);
			program.check("formal argument reveals its caller-side value", updated_sources.parents(new value_origin(value_target, 0), default).Any(parent => parent.source == new value_origin(branching, 7)));
			program.check("argument sources retain an open-world unknown remainder", updated_sources.parents(new value_origin(value_target, 0), default).Any(parent => parent.source.position < 0));
			var returning = program.audit_method(input, "Returning"); returning.MethodSig.RetType = input.CorLibTypes.Int32; returning.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldc_I4, 42)); returning.Body.UpdateInstructionOffsets(); var receiver = program.audit_method(input, "Receiver"); receiver.Body.Instructions.Clear(); receiver.Body.Instructions.Add(Instruction.Create(OpCodes.Call, returning)); receiver.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); receiver.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); receiver.Body.UpdateInstructionOffsets();
			var return_sources = new value_sources(new workspace_analysis(new[] { input }), new workspace_search(new workspace_analysis(new[] { input }), default)); program.check("return value links to callee return instructions", return_sources.parents(new value_origin(receiver, 0), default).Any(parent => parent.source == new value_origin(returning, 1)));
		}
		static void check_research_layout(call_graph_slice initial, call_graph_slice expanded, string path) {
			var surface = new call_graph_surface { Width = 1000, Height = 700 }; surface.Measure(new Size(1000, 700)); surface.Arrange(new Rect(0, 0, 1000, 700));
			var snippets = expanded.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("line();\nline();")); surface.render(initial, snippets); program.pump_ui(() => surface.is_rendering); var settled = System.Diagnostics.Stopwatch.StartNew(); program.pump_ui(() => settled.ElapsedMilliseconds < 100); var before = ((Dictionary<object, Rect>)program.ui_field(surface, "node_rects")).ToDictionary(pair => pair.Key, pair => pair.Value);
			surface.render(expanded, snippets); program.pump_ui(() => surface.is_rendering); var after = (Dictionary<object, Rect>)program.ui_field(surface, "node_rects"); program.check("expansion keeps existing card coordinates", before.All(pair => after[pair.Key].Location == pair.Value.Location));
			program.check("newly expanded cards do not overlap existing cards", after.All(pair => after.Where(other => !Equals(other.Key, pair.Key)).All(other => !other.Value.IntersectsWith(pair.Value)))); surface.clear();
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), new call_graph_index_cache(), new research_store(path + ".ui"));
			program.check("research pane is opt-in and leaves old layout intact", ((Border)control.FindName("research_pane")).Visibility == Visibility.Collapsed && ((ColumnDefinition)control.FindName("research_column")).Width.Value == 0);
			((Button)control.FindName("research_button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("research opens as an existing-style side pane", ((Border)control.FindName("research_pane")).Visibility == Visibility.Visible && ((ColumnDefinition)control.FindName("research_column")).Width.Value >= 300);
			((Button)control.FindName("close_research_button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("closing research returns its space to the graph", ((ColumnDefinition)control.FindName("research_column")).Width.Value == 0); control.dispose_graph(); File.Delete(path + ".ui");
		}
		static void check_research_controller(MethodDef root, MethodDef branch, MethodDef child, string path) {
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); settings.change_graph_depth(1);
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), new call_graph_index_cache(), new research_store(path + ".controller"));
			var surface = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(surface, "theme_service", null!);
			control.show_method(root); program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || surface.is_rendering);
			program.check("controller publishes its model even without a C# decompiler", program.ui_field(control, "research_slice") is call_graph_slice initial && initial.nodes.Any(node => node.method == branch));
			var handler = typeof(call_graph_control).GetMethod("research_requested", BindingFlags.Instance | BindingFlags.NonPublic)!;
			handler.Invoke(control, new object?[] { null, new research_action_event_args(branch, research_action.expand) });
			program.pump_ui(() => ((Button)control.FindName("cancel_research_button")).Visibility == Visibility.Visible || surface.is_rendering);
			program.check("controller expands in place through its real asynchronous command", control.current_root_value == root && ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Any(node => node.method == child));
			handler.Invoke(control, new object?[] { null, new research_action_event_args(branch, research_action.collapse) }); program.pump_ui(() => ((Button)control.FindName("cancel_research_button")).Visibility == Visibility.Visible || surface.is_rendering);
			program.check("controller collapses without rerooting or dropping the root", control.current_root_value == root && !((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Any(node => node.method == child));
			handler.Invoke(control, new object?[] { null, new research_action_event_args(root, research_action.evidence) }); program.check("evidence pane exposes original per-site IL data", ((ListBox)control.FindName("call_sites_list")).Items.Count >= 2 && ((TextBox)control.FindName("evidence_text_box")).Text.Contains("IL_", StringComparison.Ordinal));
			program.check("removed workspace-search controls are not instantiated", control.FindName("WorkspaceQueryTextBox") is null && control.FindName("WorkspaceResultsList") is null);
			handler.Invoke(control, new object?[] { null, new research_action_event_args(branch, research_action.expand) }); control.show_method(child);
			program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || ((Button)control.FindName("cancel_research_button")).Visibility == Visibility.Visible || surface.is_rendering);
			program.check("a cancelled expansion cannot replace a newer root", control.current_root_value == child && ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Single(node => node.is_root).method == child);
			control.dispose_graph(); File.Delete(path + ".controller");
		}

	}
}
