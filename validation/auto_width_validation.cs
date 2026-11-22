using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Settings;

namespace CallGraph.Validation {
	static partial class program {
		static void check_automatic_width() {
			program.check("automatic width keeps small cards compact", graph_layout.automatic_width("ret;") == 320 && graph_layout.automatic_width("") == 320);
			program.check("automatic width follows medium lines and caps long lines", graph_layout.automatic_width(new string('a', 55)) > 320 && graph_layout.automatic_width(new string('a', 55)) < 640 && graph_layout.automatic_width(new string('a', 10000)) == 640);
			program.check("automatic width handles tabs and CRLF", graph_layout.automatic_width("\t" + new string('a', 55) + "\r\nret;") == graph_layout.automatic_width("    " + new string('a', 55)));
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>());
			var page = new call_graph_settings_page(settings);
			IEnumerable<DependencyObject> descendants(DependencyObject parent) {
				yield return parent;
				foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach (var descendant in descendants(child)) yield return descendant;
			}
			var controls = descendants((DependencyObject)page.UIObject).ToArray();
			program.check("options contain neither width slider nor shortcut label", !controls.OfType<Slider>().Any() && !controls.OfType<TextBlock>().Any(text => text.Text.Contains("Alt+Q", StringComparison.Ordinal) || text.Text == ui_text.pick("Keyboard shortcut", "Горячая клавиша")));
			foreach (var type in new[] { typeof(show_tree_method_in_call_graph_menu_item), typeof(show_code_method_in_call_graph_menu_item) }) {
				var command = (MenuItemBase)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single().Invoke(new object[] { depth_services.empty<i_call_graph_service>() });
				program.check("context menu shows fixed gesture in the native right column: " + type.Name, command.GetInputGestureText(depth_services.empty<IMenuItemContext>()) == "Alt+Q");
			}
			using var input = program.audit_module("AutomaticCardWidths");
			var root = program.audit_method(input, "Root"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second"); program.audit_call(root, first); program.audit_call(root, second);
			var slice = new workspace_analysis(new[] { input }).structural_slice(root, 1, 120, call_graph_filter_options.none, default);
			var presentation = call_graph_presentation.build(slice, new Dictionary<MethodDef, highlighted_snippet>()).graph;
			var snippets = new Dictionary<object, highlighted_snippet> { [root] = highlighted_snippet.plain("return;"), [first] = highlighted_snippet.plain(new string('a', 80)), [second] = highlighted_snippet.plain(new string('a', 55)) };
			foreach (bool vertical in new[] { false, true }) {
				var layout = graph_layout.build(presentation, snippets, 640, true, vertical, default, automatic_width: true);
				program.check("auto layout uses individual card widths: vertical=" + vertical, layout.rectangles[root].Width == 320 && layout.rectangles[first].Width > layout.rectangles[second].Width && layout.rectangles[first].Width <= 640);
				program.check("auto layout cards do not overlap: vertical=" + vertical, !layout.rectangles.Any(left => layout.rectangles.Any(right => !Equals(left.Key, right.Key) && left.Value.IntersectsWith(right.Value))));
			}
			var surface = new call_graph_surface { Width = 1600, Height = 700 }; surface.configure(settings);
			surface.Measure(new Size(1600, 700)); surface.Arrange(new Rect(0, 0, 1600, 700));
			var short_code = highlighted_snippet.plain("void Run() {\n    return;\n}");
			surface.render(slice, new Dictionary<MethodDef, highlighted_snippet> { [root] = short_code, [first] = short_code, [second] = short_code }); program.pump_ui(() => surface.is_rendering);
			var rectangles = (Dictionary<object, Rect>)program.ui_field(surface, "node_rects"); var before = rectangles[first];
			surface.update_snippets(new Dictionary<MethodDef, highlighted_snippet> { [root] = highlighted_snippet.plain(new string('x', 5000)), [second] = highlighted_snippet.plain(new string('y', 55)) });
			program.check("async code growth expands only affected card widths and keeps the cap", rectangles[root].Width == 640 && rectangles[first].Width == 320 && rectangles[second].Width > 320 && rectangles[second].Width < 640);
			program.check("async width growth moves downstream columns clear of the parent", rectangles[first].Left >= rectangles[root].Right + 115 && rectangles[first].Left > before.Left);
			program.check("width updates retain all graph links", ((System.Collections.ICollection)program.ui_field(surface, "edge_visuals")).Count == slice.links.Count);
			var borders = (Dictionary<object, Border>)program.ui_field(surface, "node_borders");
			program.check("rendered border width follows its updated geometry", borders.TryGetValue(root, out var root_border) && root_border.Width == rectangles[root].Width);
			var shifted = rectangles[first]; surface.update_snippets(new Dictionary<MethodDef, highlighted_snippet> { [root] = short_code });
			program.check("shrinking a card does not pull other cards back or change their coordinates", rectangles[root].Width == 320 && rectangles[first].Location == shifted.Location);
			program.apply_options(settings, wrap: false); surface.configure(settings);
			surface.update_snippets(new Dictionary<MethodDef, highlighted_snippet> { [root] = highlighted_snippet.plain(new string('z', 5000)) });
			program.check("nonwrapped long code remains capped and exposes full-source navigation", rectangles[root].Width == 640 && ((Dictionary<object, highlighted_snippet>)program.ui_field(surface, "node_snippets"))[root].is_truncated && ((Grid)borders[root].Child).Children.OfType<Button>().Any(button => Grid.GetRow(button) == 3));
			program.apply_options(settings, wrap: true); surface.configure(settings);
			surface.clear();
			var sources = new call_graph_surface(); sources.configure_sources();
			program.check("value origin graphs retain their dedicated fixed width", !(bool)program.ui_field(sources, "automatic_width") && (double)program.ui_field(sources, "node_width") == 300); sources.clear();
		}
	}
}
