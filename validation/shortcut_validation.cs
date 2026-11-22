using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using dnlib.DotNet;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_fixed_shortcut() {
			var legacy = new Dictionary<string, object?> { ["ShortcutEnabled"] = false, ["ShortcutKey"] = 9999, ["ShortcutModifiers"] = 0 };
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>(legacy));
			program.check("legacy shortcut overrides cannot change or disable Alt+Q", !typeof(call_graph_settings).GetProperties().Any(property => property.Name.StartsWith("Shortcut", StringComparison.Ordinal)) && call_graph_command_loader.shortcut_display_text == "Alt+Q");
			using var input = program.audit_module("ShortcutGraph");
			var root = program.audit_method(input, "Root"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second"); program.audit_call(root, first); program.audit_call(root, second);
			string path = Path.Combine(Path.GetTempPath(), "CallGraph-shortcut-" + Guid.NewGuid().ToString("N") + ".json");
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), new call_graph_index_cache(), new research_store(path)) { Width = 1000, Height = 700 };
			var surface = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(surface, "theme_service", null!);
			control.Measure(new Size(1000, 700)); control.Arrange(new Rect(0, 0, 1000, 700)); control.show_method(root);
			void wait_for_graph() => program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || surface.is_rendering || ((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex == 1 && !((function_map_surface)control.FindName("function_map_surface")).is_showing(control.current_root_value!));
			wait_for_graph(); surface.focus_reference(first); surface.fit_all(); control.UpdateLayout(); var settle = System.Diagnostics.Stopwatch.StartNew(); program.pump_ui(() => settle.ElapsedMilliseconds < 100);
			program.check("card shortcut falls back to the selected method", surface.resolve_shortcut_method(null) == first);
			var borders = (Dictionary<object, Border>)program.ui_field(surface, "node_borders");
			program.check("card pointer takes priority over the previously selected method", borders.TryGetValue(second, out var second_border) && surface.resolve_shortcut_method(second_border.Child) == second);
			call_graph_command_loader.open_command.Execute(null, surface); wait_for_graph();
			program.check("the real graph command reroots from the selected card instead of the assembly tree", control.current_root_value == first);
			control.show_method(root); wait_for_graph(); ((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex = 1; wait_for_graph();
			var map = (function_map_surface)control.FindName("function_map_surface"); map.focus_method(first);
			var positions = (Dictionary<MethodDef, Point>)program.ui_field(map, "positions");
			var pointer = (Point)typeof(function_map_surface).GetMethod("graph_to_screen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, new object[] { positions[second] })!;
			program.check("map shortcut uses the pointer before selected method", map.resolve_shortcut_method(pointer) == second && map.resolve_shortcut_method(null) == first);
			call_graph_command_loader.open_command.Execute(null, map); wait_for_graph(); program.check("Alt+Q graph command is also wired in the function map", control.current_root_value == first);
			((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex = 2; wait_for_graph();
			program.check("CFG shortcut resolves the owning method rather than an IL block as a new method", surface.resolve_shortcut_method(null) == first);
			control.dispose_graph(); File.Delete(path);
			program.check_dialog_theme();
		}
		static void check_dialog_theme() {
			var (window, editor) = research_dialogs.create(new Border(), "Note", "keep this note", true);
			var style = new Style(typeof(MetroWindow));
			style.Setters.Add(new Setter(MetroWindow.ActiveCaptionProperty, new DynamicResourceExtension("DialogWindowActiveCaption")));
			style.Setters.Add(new Setter(MetroWindow.ActiveDefaultBorderProperty, new DynamicResourceExtension("DialogWindowActiveDefaultBorder")));
			window.Resources["DialogWindowStyle"] = style;
			window.Resources["DialogWindowActiveCaption"] = Brushes.DimGray; window.Resources["DialogWindowActiveDefaultBorder"] = Brushes.SteelBlue;
			window.Resources["SystemColorsWindow"] = Brushes.Black; window.Resources["SystemColorsWindowText"] = Brushes.Gainsboro; window.Resources["CommonControlsTextBoxBorder"] = Brushes.Gray;
			program.check("annotation dialog uses the native dnSpy window style including caption and border", ReferenceEquals(window.Style, style) && ReferenceEquals(window.ActiveCaption, Brushes.DimGray) && ReferenceEquals(window.ActiveDefaultBorder, Brushes.SteelBlue));
			program.check("note editor keeps text and uses dnSpy theme colors", editor.AcceptsReturn && editor.Text == "keep this note" && ReferenceEquals(editor.Background, Brushes.Black) && ReferenceEquals(editor.Foreground, Brushes.Gainsboro));
			window.Resources["DialogWindowActiveCaption"] = Brushes.DarkSlateGray;
			program.check("dialog caption follows theme resource changes without hardcoded white", ReferenceEquals(window.ActiveCaption, Brushes.DarkSlateGray));
			var buttons = ((DockPanel)window.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
			program.check("themed dialog preserves save cancel and modal keyboard behavior", buttons.Any(button => button.IsDefault) && buttons.Any(button => button.IsCancel) && !window.ShowInTaskbar);
			window.Close();
		}
	}
}
