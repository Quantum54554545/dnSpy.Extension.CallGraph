using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Updates;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_compact_layout() {
			using var input = program.audit_module("CompactLayout"); var root = program.audit_method(input, "Root");
			var branches = Enumerable.Range(0, 12).Select(index => program.audit_method(input, "branch" + index)).ToArray();
			foreach (var branch in branches) program.audit_call(root, branch);
			var tail = program.audit_method(input, "Tail"); program.audit_call(branches[5], tail);
			var slice = new workspace_analysis(new[] { input }).structural_slice(root, 2, 120, call_graph_filter_options.none, default);
			string path = Path.Combine(Path.GetTempPath(), "CallGraph-layout-" + Guid.NewGuid().ToString("N") + ".json"); var store = new research_store(path);
			store.set_positions(root, new Dictionary<string, research_position> { [research_store.key(branches[0])] = new(1000, 90000) });
			var surface = new call_graph_surface { Width = 1400, Height = 800 }; surface.configure(new call_graph_settings(depth_services.empty<ISettingsService>())); surface.set_research_store(store); surface.Measure(new Size(1400, 800)); surface.Arrange(new Rect(0, 0, 1400, 800));
			var long_code = highlighted_snippet.plain(string.Join("\n", Enumerable.Range(0, 80).Select(index => "    CallMethod" + index + "();")));
			surface.render(slice, slice.nodes.ToDictionary(node => node.method, _ => long_code)); program.pump_ui(() => surface.is_rendering);
			var rectangles = (Dictionary<object, Rect>)program.ui_field(surface, "node_rects");
			program.check("old automatic coordinates cannot restore a card at y=90000", rectangles.Values.Max(rect => rect.Bottom) < 15000);
			double old_bottom = rectangles.Values.Max(rect => rect.Bottom);
			surface.update_snippets(slice.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("return;"))); surface.fit_all(); var settle = Stopwatch.StartNew(); program.pump_ui(() => settle.ElapsedMilliseconds < 350); surface.UpdateLayout();
			program.check("offscreen code updates compact the whole layer rather than leaving old-height holes", rectangles.Values.Max(rect => rect.Bottom) < old_bottom * 0.7);
			var ordered = branches.Select(branch => rectangles[branch]).OrderBy(rect => rect.Top).ToArray();
			program.check("settled card columns have a consistent thirty-unit gap", ordered.Zip(ordered.Skip(1), (previous, next) => Math.Abs(next.Top - previous.Bottom - 30) < 1).All(value => value));
			program.check("compact layout preserves every node and edge", rectangles.Count == slice.nodes.Count && ((System.Collections.ICollection)program.ui_field(surface, "edge_visuals")).Count == slice.links.Count);
			program.check("upstream and downstream single cards align with connected neighbors", Math.Abs((rectangles[tail].Top + rectangles[tail].Height / 2) - (rectangles[branches[5]].Top + rectangles[branches[5]].Height / 2)) < 120);
			var settled = rectangles.ToDictionary(pair => pair.Key, pair => pair.Value.Location);
			foreach (double zoom in new[] { 0.35, 0.8, 1.0, 0.35 }) { surface.zoom = zoom; typeof(call_graph_surface).GetMethod("update_visible_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, null); }
			program.check("zooming does not accumulate layout drift after measurements settle", settled.All(pair => rectangles[pair.Key].Location == pair.Value));
			surface.save_positions(); program.check("automatic layout coordinates are no longer persisted", store.positions(root).Count == 0);
			var manual = (Dictionary<object, Point>)program.ui_field(surface, "manual_positions"); manual[branches[0]] = new Point(600, 1700); var moved = rectangles[branches[0]]; moved.Location = manual[branches[0]]; rectangles[branches[0]] = moved;
			surface.save_positions(); program.check("manual drag positions remain separately persisted", store.positions(root)[research_store.key(branches[0])].manual);
			surface.clear(); surface.render(slice, slice.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("return;"))); program.pump_ui(() => surface.is_rendering); settle.Restart(); program.pump_ui(() => settle.ElapsedMilliseconds < 150);
			program.check("manually positioned cards survive rerooting without restoring stale auto positions", rectangles[branches[0]].Top == 1700); surface.clear(); File.Delete(path);
			program.check_evidence_sizing(root, slice); program.check_update_acknowledgement();
		}
		static void check_evidence_sizing(MethodDef root, call_graph_slice slice) {
			string path = Path.Combine(Path.GetTempPath(), "CallGraph-evidence-" + Guid.NewGuid().ToString("N") + ".json");
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), new call_graph_settings(depth_services.empty<ISettingsService>()), depth_services.empty<IThemeService>(), new call_graph_index_cache(), new research_store(path)) { Width = 1100, Height = 800 };
			program.set_ui_field(control, "current_root", root); program.set_ui_field(control, "research_slice", slice);
			typeof(call_graph_control).GetMethod("show_method_evidence", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, new object[] { root });
			var panel = (Grid)control.FindName("evidence_panel"); var list = (ListBox)control.FindName("call_sites_list"); var detail = (TextBox)control.FindName("evidence_text_box");
			foreach (var item in new Control[] { list, detail }) { var style = new Style(item.GetType()); style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 130.0)); style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)); item.Style = style; }
			list.ItemsSource = new[] { "IL_0005: calli" };
			foreach (double height in new[] { 500.0, 760.0, 420.0 }) {
				control.Height = height; control.Measure(new Size(1100, height)); control.Arrange(new Rect(0, 0, 1100, height)); control.UpdateLayout();
				double list_bottom = list.TranslatePoint(new Point(0, list.ActualHeight), panel).Y, detail_top = detail.TranslatePoint(new Point(), panel).Y;
				program.check("evidence pane has no theme-induced vertical holes at height " + height, Math.Abs(detail_top - list_bottom - 5) < 1 && detail.ActualHeight > height / 3);
			}
			program.check("evidence pane has no redundant tabs and keeps original IL details", control.FindName("ResearchTabs") is null && control.FindName("WorkspaceSearchTab") is null && detail.Text.Contains("IL_", StringComparison.Ordinal));
			control.dispose_graph(); File.Delete(path);
		}
		static void check_update_acknowledgement() {
			string stage = Path.Combine(Path.GetTempPath(), "dnSpy.CallGraph.Update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage); using var prepared = new prepared_update(stage);
			var plan = new install_plan(Path.Combine(stage, "candidate.dll"), update_protocol.installed_path, update_protocol.hash(update_protocol.installed_path), "", update_protocol.format(update_protocol.current_version), Environment.ProcessPath!, Environment.CurrentDirectory, Array.Empty<string>(), 0, 0, Guid.NewGuid().ToString("N"));
			File.WriteAllText(prepared.plan_path, System.Text.Json.JsonSerializer.Serialize(plan)); update_installer.acknowledge_startup(prepared.plan_path);
			program.check("newly loaded plugin acknowledges the exact DLL hash and version", File.ReadAllText(Path.Combine(stage, "ack")) == plan.nonce); File.Delete(Path.Combine(stage, "ack"));
			File.WriteAllText(prepared.plan_path, System.Text.Json.JsonSerializer.Serialize(plan with { sha256 = "wrong" })); update_installer.acknowledge_startup(prepared.plan_path);
			program.check("a different DLL cannot authorize removal of the rollback copy", !File.Exists(Path.Combine(stage, "ack")));
		}
	}
}
