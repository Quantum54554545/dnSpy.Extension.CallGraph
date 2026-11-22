using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void apply_options(call_graph_settings settings, int? lines = null, bool? wrap = null, bool? system = null, bool? accessors = null, bool? constructors = null, bool? current_namespace = null, bool? current_type = null, bool? body = null, bool? clean = null, bool? assembly = null) => settings.apply(lines ?? settings.maximum_lines, wrap ?? settings.wrap_long_lines, system ?? settings.hide_system_methods, accessors ?? settings.hide_accessors, constructors ?? settings.hide_constructors, current_namespace ?? settings.only_current_namespace, current_type ?? settings.only_current_type, body ?? settings.require_method_body, clean ?? settings.clean_assembly_explorer_on_launch, assembly ?? settings.only_current_assembly);
		static void check_settings() {
			var values = new Dictionary<string, object?>(); var service = depth_services.empty<ISettingsService>(values); var settings = new call_graph_settings(service); int changes = 0; settings.changed += (_, __) => changes++;
			program.check("auto preview is 20 rows at small and large view sizes", settings.effective_maximum_lines == 20 && settings.read_maximum_lines(1) == 20 && settings.read_maximum_lines(500) == 20);
			program.apply_options(settings); program.check("applying unchanged settings does not rebuild the graph", changes == 0);
			program.apply_options(settings, lines: 37, wrap: false, system: true, accessors: true, constructors: true, current_namespace: true, current_type: true, body: true, clean: true, assembly: true); settings.change_graph_depth(4);
			var loaded = new call_graph_settings(service); program.check("all display analysis integration and depth settings round-trip", loaded.display_options == settings.display_options && loaded.analysis_options == settings.analysis_options && loaded.integration_options == settings.integration_options);
			var page = new call_graph_settings_page(settings); page.OnApply(); program.check("opening and applying options preserves custom lines and fixed single-site visibility", settings.maximum_lines == 37 && settings.minimum_call_site_count == 1 && settings.graph_depth == 4);
			foreach (int legacy in new[] { 0, 3, 1000 }) {
				var legacy_values = new Dictionary<string, object?> { ["MinimumCallSiteCount"] = legacy }; var fixed_settings = new call_graph_settings(depth_services.empty<ISettingsService>(legacy_values)); new call_graph_settings_page(fixed_settings).OnApply();
				program.check("legacy site threshold cannot hide single calls: " + legacy, fixed_settings.create_filter_options().minimum_call_site_count == 1 && Equals(legacy_values["MinimumCallSiteCount"], 1));
			}
			program.check("minimum call-site editor is absent from options", !typeof(call_graph_settings_page).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(field => field.Name.Contains("minimum_call_sites", StringComparison.Ordinal)));
			program.check("shortcut editing and disable controls are absent from options", !typeof(call_graph_settings_page).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(field => field.Name.StartsWith("shortcut_", StringComparison.Ordinal)));
			program.check("highlight checkbox and shortcut help are removed", typeof(call_graph_settings_page).GetField("highlight_check_box", BindingFlags.Instance | BindingFlags.NonPublic) is null && typeof(ui_text).GetProperty("ShortcutDescription") is null);
			var old_highlight = new Dictionary<string, object?> { ["SyntaxHighlighting"] = false }; var fixed_highlight = new call_graph_settings(depth_services.empty<ISettingsService>(old_highlight)); new call_graph_settings_page(fixed_highlight).OnApply(); program.check("legacy disabled highlighting cannot disable the native dnSpy palette", fixed_highlight.syntax_highlighting && Equals(old_highlight["SyntaxHighlighting"], true));
			var flags = new[] { "wrap_check_box", "hide_system_methods_check_box", "only_current_assembly_check_box", "hide_accessors_check_box", "hide_constructors_check_box", "only_current_namespace_check_box", "only_current_type_check_box", "require_method_body_check_box", "clean_explorer_check_box" };
			foreach (string field in flags) ((CheckBox)program.ui_field(page, field)).IsChecked = field == "wrap_check_box";
			page.OnApply();
			program.check("options page keeps automatic width and applies wrapping and highlighting", settings.block_width == graph_layout.maximum_automatic_width && settings.wrap_long_lines && settings.syntax_highlighting);
			program.check("options page applies all seven graph filter controls", !settings.hide_system_methods && !settings.only_current_assembly && !settings.hide_accessors && !settings.hide_constructors && !settings.only_current_namespace && !settings.only_current_type && !settings.require_method_body);
			program.check("options page applies the integration checkbox", !settings.clean_assembly_explorer_on_launch);
			values["future_option"] = "keep"; program.apply_options(settings); program.check("applying settings does not erase unrelated future attributes", Equals(values["future_option"], "keep"));
			foreach (double invalid_width in new[] { 320.0, 900.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity }) {
				var invalid = new Dictionary<string, object?> { ["BlockWidth"] = invalid_width }; var normalized = new call_graph_settings(depth_services.empty<ISettingsService>(invalid)); new call_graph_settings_page(normalized).OnApply(); program.check("nonfinite width cannot break the WPF options page: " + invalid_width, normalized.block_width == graph_layout.maximum_automatic_width);
			}
			program.apply_options(settings, lines: 999); program.check("API settings clamp invalid values", settings.maximum_lines == 500 && settings.block_width == graph_layout.maximum_automatic_width && settings.minimum_call_site_count == 1);
			program.check_graph_filter_settings(); program.check_filter_policies(); program.check_preview_settings(); program.check_integration_settings(); program.check_fixed_shortcut(); program.check_automatic_width();
		}
		static void check_graph_filter_settings() {
			using var input = program.audit_module("SettingsFilters"); using var external = program.audit_module("SettingsExternal"); using var framework = ModuleDefMD.Load(typeof(object).Assembly.Location);
			var root = program.audit_method(input, "Root"); var repeated = program.audit_method(input, "Repeated"); var getter = program.audit_method(input, "Property"); getter.SemanticsAttributes = dnlib.DotNet.MethodSemanticsAttributes.Getter;
			var constructor = program.audit_method(input, "Constructor"); constructor.Name = ".ctor"; var native = program.audit_method(input, "Native"); native.Body = null;
			var other_namespace = program.audit_method(input, "ForeignNamespace"); other_namespace.DeclaringType.Namespace = "Elsewhere";
			var nested = program.audit_method(input, "Nested"); var nested_type = nested.DeclaringType; input.Types.Remove(nested_type); nested_type.Namespace = ""; root.DeclaringType.NestedTypes.Add(nested_type);
			var same_type = new MethodDefUser("SameType", MethodSig.CreateStatic(input.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; same_type.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); root.DeclaringType.Methods.Add(same_type);
			var system = framework.GetTypes().Single(type => type.FullName == "System.Object").Methods.First(method => method.Name == "GetHashCode");
			foreach (var target in new[] { repeated, repeated, getter, constructor, native, other_namespace, nested, same_type, system }) program.audit_call(root, target);
			var outside = program.audit_method(external, "Outside"); var outside_child = program.audit_method(external, "OutsideChild"); program.audit_call(root, outside); program.audit_call(outside, outside_child);
			var outside_caller = program.audit_method(external, "OutsideCaller"); var caller_parent = program.audit_method(external, "CallerParent"); program.audit_call(outside_caller, root); program.audit_call(caller_parent, outside_caller);
			var workspace = new workspace_analysis(new ModuleDef[] { input, external });
			call_graph_slice slice(call_graph_filter_options filter) => workspace.structural_slice(root, 3, 120, filter, default);
			program.check("hide accessors removes getter edges but not unrelated methods", !program.has(slice(new call_graph_filter_options(hide_accessors: true)), getter) && program.has(slice(new call_graph_filter_options(hide_accessors: true)), repeated));
			program.check("hide constructors applies without hiding the selected root", !program.has(slice(new call_graph_filter_options(hide_constructors: true)), constructor));
			program.check("require body excludes native method cards", !program.has(slice(new call_graph_filter_options(require_method_body: true)), native));
			var namespace_slice = slice(new call_graph_filter_options(only_current_namespace: true)); program.check("namespace filter uses outer namespace for nested types", program.has(namespace_slice, nested) && !program.has(namespace_slice, other_namespace));
			var type_slice = slice(new call_graph_filter_options(only_current_type: true)); program.check("current type filter distinguishes nested and separate declaring types", program.has(type_slice, same_type) && !program.has(type_slice, nested) && !program.has(type_slice, repeated));
			var minimum = slice(new call_graph_filter_options(minimum_call_site_count: 2)); program.check("minimum site setting counts distinct IL offsets", program.has(minimum, repeated) && !program.has(minimum, getter) && minimum.links.Single(link => link.target == repeated).call_site_count == 2);
			var legacy_settings = new call_graph_settings(depth_services.empty<ISettingsService>(new Dictionary<string, object?> { ["MinimumCallSiteCount"] = 3 })); var unfiltered_sites = slice(legacy_settings.create_filter_options());
			program.check("saved legacy threshold three preserves both single-site and repeated calls in the production filter", program.has(unfiltered_sites, getter) && program.has(unfiltered_sites, repeated) && unfiltered_sites.links.Single(link => link.target == getter).call_site_count == 1);
			var system_slice = slice(new call_graph_filter_options(hide_system_methods: true)); program.check("system filter recognizes framework identity without hiding user code", !program.has(system_slice, system) && program.has(system_slice, repeated));
			var scoped = slice(new call_graph_filter_options(only_current_assembly: true)); program.check("assembly traversal keeps external calls as leaves", program.has(scoped, outside) && !program.has(scoped, outside_child));
			var index = call_graph_index.combine(new[] { call_graph_index.build(input, default, workspace), call_graph_index.build(external, default, workspace) }, default, workspace); var incoming = index.select(root, call_graph_direction.callers, 3, 120, new call_graph_filter_options(only_current_assembly: true), default, workspace, true);
			program.check("assembly traversal is consistent for incoming external leaves", incoming.nodes.Any(node => node.method == outside_caller) && !incoming.nodes.Any(node => node.method == caller_parent) && incoming.diagnostics.Any(note => note.kind == graph_diagnostic_kind.assembly_boundary));
			foreach (var filter in new[] { new call_graph_filter_options(hide_accessors: true), new call_graph_filter_options(hide_constructors: true), new call_graph_filter_options(require_method_body: true), new call_graph_filter_options(only_current_namespace: true), new call_graph_filter_options(only_current_type: true), new call_graph_filter_options(minimum_call_site_count: 2), new call_graph_filter_options(hide_system_methods: true) }) {
				var original = slice(filter); var model = new exploration_graph(root, call_graph_direction.callees, 120, filter); model.merge(original, true); var projected = model.project(); program.check("exploration projection preserves active filter: " + string.Join(",", original.nodes.Count, original.links.Count), projected.nodes.Select(node => node.method).ToHashSet().SetEquals(original.nodes.Select(node => node.method)));
			}
		}
		static void check_preview_settings() {
			var short_code = highlighted_snippet.plain("void Run() {\n    Target();\n}"); program.check("small methods remain complete", ReferenceEquals(short_code, short_code.preview(20, 430, true)) && !short_code.is_truncated);
			string text = string.Join("\n", Enumerable.Range(0, 80).Select(index => "line" + index + "();")); var code = new highlighted_snippet(text, new[] { new colored_text_span(0, text.Length, TextColor.StaticMethod) }); var preview = code.preview(20, 430, true);
			program.check("large cards stop at preview budget without losing cached full text", preview.is_truncated && !preview.text.Contains("line79", StringComparison.Ordinal) && code.text.Contains("line79", StringComparison.Ordinal));
			program.check("preview cropping keeps all syntax spans valid", preview.spans.All(span => span.start >= 0 && span.length >= 0 && span.start + span.length <= preview.text.Length));
			var wide = highlighted_snippet.plain(new string('x', 10000)); program.check("one long wrapped line cannot create an enormous card", wide.preview(20, 320, true).text.Length < 1200 && wide.preview(20, 320, true).is_truncated);
			program.check("nonwrapped clipped text still advertises full-source navigation", wide.preview(20, 320, false).text == wide.text && wide.preview(20, 320, false).is_truncated);
			var output = new highlighted_decompiler_output(3); try { output.Write("one\ntwo\nthree\nfour", TextColor.Text); } catch (snippet_limit_reached_exception) { }
			program.check("multiline decompiler writes obey the selected line count", output.create_snippet().is_truncated && !output.create_snippet().text.Contains("four", StringComparison.Ordinal));
			var huge_output = new highlighted_decompiler_output(20, 100); try { huge_output.Write(new string('a', 500), TextColor.String); } catch (snippet_limit_reached_exception) { }
			program.check("a large first token retains a prefix rather than claiming no method body", huge_output.create_snippet().is_truncated && huge_output.create_snippet().text.StartsWith(new string('a', 100), StringComparison.Ordinal));
			using var input = program.audit_module("PreviewSettings"); var root = program.audit_method(input, "Root"); var target = program.audit_method(input, "Target"); program.audit_call(root, target); var workspace = new workspace_analysis(new[] { input }); var slice = workspace.structural_slice(root, 1, 120, call_graph_filter_options.none, default);
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); var surface = new call_graph_surface { Width = 1000, Height = 700 }; surface.configure(settings); surface.Measure(new Size(1000, 700)); surface.Arrange(new Rect(0, 0, 1000, 700)); surface.render(slice, new Dictionary<MethodDef, highlighted_snippet> { [root] = code, [target] = short_code }); program.pump_ui(() => surface.is_rendering); var settled = System.Diagnostics.Stopwatch.StartNew(); program.pump_ui(() => settled.ElapsedMilliseconds < 100);
			var previews = (Dictionary<object, highlighted_snippet>)program.ui_field(surface, "node_snippets"); program.check("WPF cards use bounded previews but retain every graph edge", previews[root].is_truncated && !previews[target].is_truncated && ((System.Collections.ICollection)program.ui_field(surface, "edge_visuals")).Count == slice.links.Count);
			surface.fit_all(); surface.UpdateLayout(); settled.Restart(); program.pump_ui(() => settled.ElapsedMilliseconds < 100); surface.UpdateLayout();
			var borders = (Dictionary<object, Border>)program.ui_field(surface, "node_borders"); var root_grid = (Grid)borders[root].Child; var target_grid = (Grid)borders[target].Child;
			program.check("only shortened cards have a full-source button", root_grid.Children.OfType<Button>().Any(button => Grid.GetRow(button) == 3) && !target_grid.Children.OfType<Button>().Any(button => Grid.GetRow(button) == 3));
			bool requested = false; surface.navigate_requested += (_, event_args) => requested = event_args.reference == root; root_grid.Children.OfType<Button>().Single(button => Grid.GetRow(button) == 3).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("inline preview continuation opens native source navigation", requested);
			surface.update_snippets(new Dictionary<MethodDef, highlighted_snippet> { [root] = short_code }); surface.update_snippets(new Dictionary<MethodDef, highlighted_snippet> { [root] = code });
			program.check("repeated code updates do not duplicate code or footer controls", root_grid.Children.OfType<FrameworkElement>().Count(child => Grid.GetRow(child) == 2) == 1 && root_grid.Children.OfType<FrameworkElement>().Count(child => Grid.GetRow(child) == 3) == 1);
			var menu = (ContextMenu)typeof(call_graph_surface).GetMethod("create_node_context_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, new object[] { ((Dictionary<object, graph_node_presentation>)program.ui_field(surface, "node_presentations"))[root] })!;
			program.check("hide and restore-hidden commands are removed from the UI", !menu.Items.OfType<MenuItem>().Any(item => item.Header.ToString()!.StartsWith("Скрыть ", StringComparison.OrdinalIgnoreCase)) && !surface.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Header.ToString()!.Contains("скрыт", StringComparison.OrdinalIgnoreCase)));
			surface.clear(); program.check_settings_impact(root, target);
		}
		static void check_settings_impact(MethodDef root, MethodDef branch) {
			var child = program.audit_method(root.Module, "Child"); program.audit_call(branch, child);
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); settings.change_graph_depth(1); string path = Path.Combine(Path.GetTempPath(), "CallGraph-settings-" + Guid.NewGuid().ToString("N") + ".json");
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), new call_graph_index_cache(), new research_store(path)); var surface = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(surface, "theme_service", null!);
			void wait_for_graph() => program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || surface.is_rendering || ((Button)control.FindName("cancel_research_button")).Visibility == Visibility.Visible);
			control.show_method(root); wait_for_graph(); var command = typeof(call_graph_control).GetMethod("research_requested", BindingFlags.Instance | BindingFlags.NonPublic)!;
			command.Invoke(control, new object?[] { null, new research_action_event_args(branch, research_action.expand) }); wait_for_graph(); program.check("settings test starts with an expanded extra level", ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Any(node => node.method == child));
			program.apply_options(settings, lines: 5, wrap: false); wait_for_graph(); program.check("display changes preserve in-place expansion and selected root", control.current_root_value == root && ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Any(node => node.method == child));
			program.check("all render settings reach the card surface", (double)program.ui_field(surface, "node_width") == graph_layout.maximum_automatic_width && !(bool)program.ui_field(surface, "wrap_long_lines") && (bool)program.ui_field(surface, "syntax_highlighting") && (int)program.ui_field(surface, "card_preview_lines") == 5);
			program.check("native highlighting stays enabled in function map previews", (bool)program.ui_field((function_map_surface)control.FindName("function_map_surface"), "syntax_highlighting"));
			var model = program.ui_field(control, "exploration"); program.apply_options(settings, clean: true); program.check("integration-only changes do not recreate the graph model", ReferenceEquals(model, program.ui_field(control, "exploration")) && !(bool)program.ui_field(control, "analyzing_graph"));
			program.apply_options(settings, current_type: true); wait_for_graph(); program.check("analysis filter changes invalidate the old expanded view", ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Count == 1);
			((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex = 2; wait_for_graph(); program.check("display settings remain active in the CFG mode", (bool)program.ui_field(surface, "vertical_layout") && (double)program.ui_field(surface, "node_width") == graph_layout.maximum_automatic_width && (bool)program.ui_field(surface, "syntax_highlighting"));
			program.check("research pane contains evidence only without search or code tabs", control.FindName("ResearchTabs") is null && control.FindName("WorkspaceSearchTab") is null && control.FindName("evidence_panel") is Grid && control.FindName("InspectorTab") is null);
			control.dispose_graph(); File.Delete(path);
		}
		static void check_integration_settings() {
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); var window = new Window();
			var app = depth_services.empty<IAppWindow>(new Dictionary<string, object?> { ["$get_MainWindow"] = window });
			var loader = (call_graph_command_loader)typeof(call_graph_command_loader).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single().Invoke(new object[] { depth_services.empty<IWpfCommandService>(), app, new Lazy<i_call_graph_service>(() => depth_services.empty<i_call_graph_service>()) });
			program.check("fixed Alt+Q is registered in the app window", window.InputBindings.OfType<KeyBinding>().Single() is var initial && initial.Key == Key.Q && initial.Modifiers == ModifierKeys.Alt);
			var binding = window.InputBindings[0]; program.apply_options(settings, lines: 5); program.check("applying unrelated options neither changes nor duplicates the fixed binding", window.InputBindings.Count == 1 && ReferenceEquals(binding, window.InputBindings[0])); window.Close();
			var calls = new List<string>(); var documents = depth_services.empty<IDocumentTabService>(new Dictionary<string, object?> { ["$trace"] = calls });
			var cleaner = (assembly_explorer_cleaner)typeof(assembly_explorer_cleaner).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single().Invoke(new object[] { settings, documents }); var args = depth_services.empty<IAppCommandLineArgs>(); cleaner.OnNewArgs(args); program.check("disabled explorer cleanup never closes tabs", !calls.Contains("CloseAll"));
			program.apply_options(settings, clean: true); cleaner.OnNewArgs(args); program.check("enabling launch cleanup does not destroy the running session", !calls.Contains("CloseAll"));
			cleaner = (assembly_explorer_cleaner)typeof(assembly_explorer_cleaner).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single().Invoke(new object[] { settings, documents }); cleaner.OnNewArgs(args); program.check("enabled cleanup clears restored tabs and documents for an empty launch", calls.Contains("CloseAll") && calls.Contains("Clear"));
			var file_args = depth_services.empty<IAppCommandLineArgs>(new Dictionary<string, object?> { ["$get_Filenames"] = new[] { "C:\\Temp\\example.exe" } });
			calls.Clear(); cleaner.OnNewArgs(file_args); program.check("later command-line files do not close existing tabs or remove documents", !calls.Contains("CloseAll") && !calls.Contains("Clear") && !calls.Contains("Remove"));
			cleaner = (assembly_explorer_cleaner)typeof(assembly_explorer_cleaner).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single().Invoke(new object[] { settings, documents }); cleaner.OnNewArgs(file_args); program.check("initial command-line cleanup does not clear the whole document service", calls.Contains("CloseAll") && !calls.Contains("Clear"));
		}
	}
}
