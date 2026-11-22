using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using dnlib.DotNet;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;

namespace CallGraph.Validation {
	static partial class program {
		sealed class gated_snippets : IReadOnlyDictionary<MethodDef, highlighted_snippet> {
			readonly IReadOnlyDictionary<MethodDef, highlighted_snippet> values;
			readonly ManualResetEventSlim entered;
			readonly ManualResetEventSlim release;
			public gated_snippets(IReadOnlyDictionary<MethodDef, highlighted_snippet> values, ManualResetEventSlim entered, ManualResetEventSlim release) { this.values = values; this.entered = entered; this.release = release; }
			public highlighted_snippet this[MethodDef key] => this.values[key];
			public IEnumerable<MethodDef> Keys => this.values.Keys;
			public IEnumerable<highlighted_snippet> Values => this.values.Values;
			public int Count => this.values.Count;
			public bool ContainsKey(MethodDef key) => this.values.ContainsKey(key);
			public bool TryGetValue(MethodDef key, out highlighted_snippet value) => this.values.TryGetValue(key, out value!);
			public IEnumerator<KeyValuePair<MethodDef, highlighted_snippet>> GetEnumerator() { this.entered.Set(); if (!this.release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Presentation worker was not released"); return this.values.GetEnumerator(); }
			IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
		}
		static void check_presentation_worker() {
			using var input = program.audit_module("PresentationWorker"); var old_root = program.audit_method(input, "Old"); var new_root = program.audit_method(input, "New");
			var slice = new call_graph_slice(new[] { new call_graph_node(old_root, 0, true, false) }, Array.Empty<call_graph_link>(), false);
			using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var surface = new call_graph_surface();
			try {
				surface.render(slice, new gated_snippets(new Dictionary<MethodDef, highlighted_snippet> { [old_root] = highlighted_snippet.plain("old") }, entered, release));
				program.check("card presentation is prepared in a worker rather than synchronously in render", entered.Wait(TimeSpan.FromSeconds(2)) && !release.IsSet && surface.is_rendering);
				int pulses = 0; var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(1), System.Windows.Threading.DispatcherPriority.Input, (_, __) => pulses++, System.Windows.Threading.Dispatcher.CurrentDispatcher);
				var clock = Stopwatch.StartNew(); program.pump_ui(() => clock.ElapsedMilliseconds < 60); timer.Stop(); program.check("UI input continues while card presentation preparation is occupied", pulses > 0);
				surface.render(new call_graph_slice(new[] { new call_graph_node(new_root, 0, true, false) }, Array.Empty<call_graph_link>(), false), new Dictionary<MethodDef, highlighted_snippet> { [new_root] = highlighted_snippet.plain("new") });
			} finally { release.Set(); }
			program.pump_ui(() => surface.is_rendering); program.check("cancelled card presentation cannot replace a newer graph", surface.is_showing(new_root)); surface.clear();
			var offsets = Enumerable.Range(0, 5000).Select(value => (uint)value).ToArray();
			var dense = new call_graph_slice(new[] { new call_graph_node(old_root, 0, true, false), new call_graph_node(new_root, 1, false, false) }, new[] { new call_graph_link(old_root, new_root, offsets.Length, offsets, new[] { "call" }) }, false);
			var rendered = call_graph_presentation.build(dense, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("large edge tooltips stay bounded without dropping call-site evidence or counts", dense.links[0].evidence.Count == 5000 && rendered.graph.links.Single().call_site_count == 5000 && rendered.graph.links.Single().tool_tip.Length < 10000);
		}
		static void check_render_reliability() {
			SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
			program.check_presentation_worker();
			using var input = program.audit_module("DenseRendering");
			var methods = Enumerable.Range(0, 120).Select(index => program.audit_method(input, "Method" + index)).ToArray();
			var nodes = methods.Select((method, index) => new call_graph_node(method, index < 60 ? 0 : 1, index == 0, false)).ToArray();
			var links = methods.Take(60).SelectMany(source => methods.Skip(60).Select(target => new call_graph_link(source, target, 1, new uint[] { 5 }, new[] { "callvirt" }, target == methods[119]))).ToArray();
			var slice = new call_graph_slice(nodes, links, false);
			var snippets = methods.ToDictionary(method => method, _ => highlighted_snippet.plain("Call();"));
			var view = call_graph_presentation.build(slice, snippets);
			var layout = graph_layout.build(view.graph, view.snippets, 640, true, false, default, true);
			var first = layout.horizontal!.compact(layout.rectangles, default); var second = layout.horizontal.compact(layout.rectangles, default);
			program.check("indexed dense compaction is deterministic and does not mutate its input", first.All(pair => pair.Value == second[pair.Key]) && layout.rectangles.All(pair => pair.Value == first[pair.Key]));
			program.check("indexed layout preserves dense topology and column gaps", first.Count == 120 && nodes.GroupBy(node => node.layer).All(group => group.Select(node => first[node.method]).OrderBy(rect => rect.Top).Zip(group.Select(node => first[node.method]).OrderBy(rect => rect.Top).Skip(1), (left, right) => right.Top >= left.Bottom + 29.9).All(value => value)));
			using (var cancel = new CancellationTokenSource()) {
				cancel.Cancel(); bool cancelled = false; try { layout.horizontal.compact(first, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
				program.check("indexed layout cooperates with cancellation", cancelled);
			}
			var surface = new call_graph_surface { Width = 1400, Height = 800 }; surface.Measure(new Size(1400, 800)); surface.Arrange(new Rect(0, 0, 1400, 800));
			surface.render(slice, snippets); program.pump_ui(() => surface.is_rendering);
			var edges = ((IEnumerable)program.ui_field(surface, "edge_visuals")).Cast<object>().ToArray();
			program.check("all 3600 dense graph links and their unresolved remainders survive batched rendering", edges.Length == view.graph.links.Count && edges.Count(edge => ((graph_link_presentation)edge.GetType().GetProperty("link")!.GetValue(edge)!).target is MethodDef) == 3600);
			program.check("rendered curves use frozen lightweight geometries", edges.All(edge => edge.GetType().GetProperty("curve")!.GetValue(edge) is StreamGeometry { IsFrozen: true }));
			var starts = edges.Take(60).Select(edge => PathGeometry.CreateFromGeometry((Geometry)edge.GetType().GetProperty("curve")!.GetValue(edge)!).Figures[0].StartPoint).ToArray();
			program.check("dense source ports stay distinct without per-edge linear searches", starts.Distinct().Count() == 60);
			var settle = Stopwatch.StartNew(); program.pump_ui(() => settle.ElapsedMilliseconds < 150);
			var cached = (Dictionary<object, Border>)program.ui_field(surface, "cached_borders");
			var original = cached.ToDictionary(pair => pair.Key, pair => pair.Value);
			var update = typeof(call_graph_surface).GetMethod("update_visible_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!;
			for (int pass = 0; pass < 5; pass++) update.Invoke(surface, null);
			program.check("settled visibility updates reuse existing cards", original.All(pair => !cached.TryGetValue(pair.Key, out var border) || ReferenceEquals(pair.Value, border)));
			surface.clear();
			program.check("clear releases port indices and cached topology", ((ICollection)program.ui_field(surface, "port_fractions")).Count == 0 && ((ICollection)program.ui_field(surface, "outgoing_ports")).Count == 0 && program.ui_field(surface, "horizontal_layout") is null);
			var map = new function_map_surface { Width = 1400, Height = 800 }; map.Measure(new Size(1400, 800)); map.Arrange(new Rect(0, 0, 1400, 800)); map.render(slice); program.pump_ui(() => !map.is_showing(methods[0]));
			var draw = typeof(function_map_surface).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
			void draw_value() { var group = new DrawingGroup(); using var context = group.Open(); draw.Invoke(map, new object[] { context }); }
			draw_value(); int drawings = (int)program.ui_field(map, "graph_draw_count");
			for (int frame = 0; frame < 12; frame++) { program.set_ui_field(map, "pan", new Vector(frame * 5, frame * 3)); draw_value(); }
			program.check("map panning reuses graph drawing rather than rebuilding geometry", (int)program.ui_field(map, "graph_draw_count") == drawings);
			var texts = ((IEnumerable)program.ui_field(map, "text_cache")).Cast<object>().Count();
			program.check("map text cache stays bounded while panning", texts > 0 && texts <= 128);
			program.set_ui_field(map, "hovered", methods[5]); program.set_ui_field(map, "preview_method", methods[5]); map.set_code_preview(methods[5], highlighted_snippet.plain("return 42;")); draw_value(); var code = program.ui_field(map, "preview_code"); draw_value();
			program.check("map preview reuses formatted code between frames", ReferenceEquals(code, program.ui_field(map, "preview_code")));
			map.set_code_preview(methods[5], highlighted_snippet.plain("return 77;")); draw_value(); program.check("map preview invalidates when source changes", !ReferenceEquals(code, program.ui_field(map, "preview_code")));
			typeof(function_map_surface).GetMethod("build_geometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, null);
			map.clear(); var replacement = new call_graph_slice(new[] { new call_graph_node(methods[119], 0, true, false) }, Array.Empty<call_graph_link>(), false); map.render(replacement); program.pump_ui(() => !map.is_showing(methods[119]));
			settle.Restart(); program.pump_ui(() => settle.ElapsedMilliseconds < 100);
			program.check("cancelled map geometry cannot replace a newly selected root", ((ICollection)program.ui_field(map, "positions")).Count == 1 && ((IEnumerable)program.ui_field(map, "edge_geometry")).Cast<object>().Count() == 0);
			map.clear(); program.check("clear releases formatted map text and drawings", ((ICollection)program.ui_field(map, "text_cache")).Count == 0 && ((ICollection)program.ui_field(map, "text_drawings")).Count == 0);
		}
	}
}
