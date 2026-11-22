using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;

namespace CallGraph.Validation {
	static partial class program {
		static int render_performance() {
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
			Console.WriteLine($"Render CPU benchmark {typeof(graph_layout).Assembly.GetName().Version}; {Environment.Version}; {Environment.ProcessorCount} logical CPUs; offscreen WPF, not GPU frame time");
			using var input = program.audit_module("RenderPerformance");
			foreach (int count in new[] { 120, 500, 2000 }) {
				var methods = Enumerable.Range(0, count).Select(index => program.audit_method(input, "Node" + count + "_" + index)).ToArray();
				var nodes = methods.Select((method, index) => new call_graph_node(method, index == 0 ? 0 : 1 + (index - 1) / 60, index == 0, false)).ToArray();
				var links = methods.Skip(1).Select(method => new call_graph_link(methods[0], method, 1, new uint[] { 0 }, new[] { "call" })).ToArray();
				var slice = new call_graph_slice(nodes, links, false);
				var snippets = methods.ToDictionary(method => method, _ => highlighted_snippet.plain(string.Join("\n", Enumerable.Repeat("    int value = Method(argument);", 20))));
				var view = call_graph_presentation.build(slice, snippets);
				var rectangles = graph_layout.build(view.graph, view.snippets, 640, true, false, default, true).rectangles;
				program.measure_render($"layout nodes={count} edges={links.Length}", () => graph_layout.build(view.graph, view.snippets, 640, true, false, default, true));
				program.measure_render($"compact nodes={count}", () => graph_layout.compact_horizontal(view.graph, rectangles, default));
				program.measure_render($"map-layout nodes={count}", () => graph_layout.map(slice, default));
				var surface = new call_graph_surface { Width = 1400, Height = 800 };
				surface.Measure(new Size(1400, 800)); surface.Arrange(new Rect(0, 0, 1400, 800));
				var initial = Stopwatch.StartNew(); surface.render(slice, snippets); program.pump_ui(() => surface.is_rendering); Console.WriteLine($"INITIAL blocks nodes={count} completed={!surface.is_rendering} ms={initial.Elapsed.TotalMilliseconds:F3}");
				var settled = Stopwatch.StartNew(); program.pump_ui(() => settled.ElapsedMilliseconds < 400);
				surface.UpdateLayout(); surface.center_root(); surface.UpdateLayout(); settled.Restart(); program.pump_ui(() => settled.ElapsedMilliseconds < 400); surface.UpdateLayout();
				var visibility = typeof(call_graph_surface).GetMethod("update_visible_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!;
				program.measure_render($"visibility nodes={count}", () => visibility.Invoke(surface, null));
				Console.WriteLine($"STATE nodes={count} cards={((ICollection)program.ui_field(surface, "node_borders")).Count} cached={((ICollection)program.ui_field(surface, "cached_borders")).Count} measured={((ICollection)program.ui_field(surface, "measured_snippets")).Count} zoom={surface.zoom}");
				program.measure_render($"geometry-update nodes={count}", () => typeof(call_graph_surface).GetMethod("apply_geometry_changes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, new object[] { new HashSet<object> { methods[0] } }));
				program.check("render benchmark retains block edges " + count, ((ICollection)program.ui_field(surface, "edge_visuals")).Count == links.Length);
				var map = new function_map_surface { Width = 1400, Height = 800 };
				map.Measure(new Size(1400, 800)); map.Arrange(new Rect(0, 0, 1400, 800)); initial.Restart(); map.render(slice); program.pump_ui(() => !map.is_showing(methods[0])); Console.WriteLine($"INITIAL map nodes={count} completed={map.is_showing(methods[0])} ms={initial.Elapsed.TotalMilliseconds:F3}");
				var overlay = typeof(function_map_surface).GetMethod("draw_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
				program.measure_render($"map-overlay nodes={count}", () => { var drawing = new DrawingGroup(); using var context = drawing.Open(); overlay.Invoke(map, new object[] { context }); });
				program.check("render benchmark retains map nodes " + count, ((ICollection)program.ui_field(map, "positions")).Count == count);
				surface.clear(); map.clear();
			}
			return program.failures == 0 ? 0 : 1;
		}
		static void measure_render(string label, Action action) {
			for (int warm = 0; warm < 4; warm++) action();
			var times = new double[31]; long allocated = GC.GetAllocatedBytesForCurrentThread();
			for (int sample = 0; sample < times.Length; sample++) { long start = Stopwatch.GetTimestamp(); action(); times[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
			long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated; Array.Sort(times);
			Console.WriteLine($"PERF {label}: p50={times[15]:F3}ms p95={times[29]:F3}ms allocated={bytes / times.Length}B/op");
		}
	}
}
