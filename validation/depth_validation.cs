using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	public class depth_services : DispatchProxy {
		public Dictionary<string, object?> values { get; set; } = new();
		public static type_parameter empty<type_parameter>(Dictionary<string, object?>? values = null) where type_parameter : class {
			var service = Create<type_parameter, depth_services>(); if (values is not null) ((depth_services)(object)service).values = values; return service;
		}
		protected override object? Invoke(MethodInfo? method, object?[]? arguments) {
			if (this.values.TryGetValue("$callback:" + method!.Name, out var callback) && callback is Func<object?[]?, object?> invoke) return invoke(arguments);
			if (this.values.TryGetValue("$trace", out var trace) && trace is List<string> calls) calls.Add(method!.Name);
			if (this.values.TryGetValue("$" + method!.Name, out var result)) return result;
			if (method!.Name == "Attribute" && arguments is { Length: 2 }) { this.values[(string)arguments[0]!] = arguments[1]; return null; }
			if (method.Name == "Attribute" && arguments is { Length: 1 } && this.values.TryGetValue((string)arguments[0]!, out var value)) return value;
			if (method.Name == "RecreateSection") this.values.Clear();
			var type = method.ReturnType; if (type == typeof(void)) return null; if (type == typeof(string)) return "";
			if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return Array.CreateInstance(type.GetGenericArguments()[0], 0);
			if (type.IsInterface) { var service = Create(type, typeof(depth_services)); ((depth_services)service).values = this.values; return service; }
			return type.IsValueType ? Activator.CreateInstance(type) : null;
		}
	}
	static partial class program {
		static void check_depth_selection() {
			using var input = program.audit_module("DepthSelection");
			var methods = Enumerable.Range(0, 11).Select(index => program.audit_method(input, "Level" + index)).ToArray();
			for (int index = 0; index < methods.Length - 1; index++) program.audit_call(methods[index], methods[index + 1]);
			var workspace = new workspace_analysis(new[] { input });
			var shallow = workspace.browse(methods[0], 120, call_graph_filter_options.none, default, depth: 1).slice;
			var deep = workspace.browse(methods[0], 120, call_graph_filter_options.none, default, depth: 8).slice;
			program.check("selected depth one stops after one call level", shallow.nodes.Count == 2 && program.has(shallow, methods[1]) && !program.has(shallow, methods[2]));
			program.check("selected depth eight is not silently reduced to two", deep.nodes.Count == 9 && program.has(deep, methods[8]) && !program.has(deep, methods[9]));
			program.check("depth boundary reports the selected value", deep.diagnostics.Any(note => note.kind == graph_diagnostic_kind.view_depth && note.detail.Contains("8", StringComparison.Ordinal)));
			var cached_shallow = workspace.browse(methods[0], 120, call_graph_filter_options.none, default, depth: 1).slice;
			program.check("query caches distinguish depth one and eight without rerunning refinement", !ReferenceEquals(shallow, deep) && cached_shallow.nodes.Select(node => node.method).ToHashSet().SetEquals(shallow.nodes.Select(node => node.method)) && workspace.analysis_count == 2);
			program.check("changing depth does not remove the independent card limit", workspace.browse(methods[0], 3, call_graph_filter_options.none, default, depth: 8).slice.nodes.Count == 3);
			program.check("browser depth is clamped to eight", ReferenceEquals(deep, workspace.browse(methods[0], 120, call_graph_filter_options.none, default, depth: 100).slice));
			var values = new Dictionary<string, object?>(); var service = depth_services.empty<ISettingsService>(values); var settings = new call_graph_settings(service);
			program.check("GUI depth defaults to eight", settings.graph_depth == 8);
			int changes = 0; settings.changed += (_, __) => changes++;
			settings.change_graph_depth(5); settings.change_graph_depth(5);
			program.check("depth changes persist without duplicate notifications", new call_graph_settings(service).graph_depth == 5 && changes == 1);
			settings.apply(0, true, false, false, false, false, false, false, false);
			program.check("applying unrelated settings preserves the saved depth", new call_graph_settings(service).graph_depth == 5);
			settings.change_graph_depth(99); program.check("saved depth cannot exceed eight", new call_graph_settings(service).graph_depth == 8);
			settings.change_graph_depth(-1); program.check("saved depth cannot be below one", new call_graph_settings(service).graph_depth == 1);
			settings.change_graph_depth(5);
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), new call_graph_index_cache());
			var selector = (ComboBox)control.FindName("depth_combo_box"); var mode = (ComboBox)control.FindName("view_mode_combo_box");
			var view_limit = typeof(call_graph_control).GetProperty("current_view_limit", BindingFlags.Instance | BindingFlags.NonPublic)!;
			program.check("limit label and selector are absent from the GUI", control.FindName("LimitLabel") is null && control.FindName("NodeLimitComboBox") is null);
			program.check("call blocks use the fixed limit of 120", Equals(view_limit.GetValue(control), 120));
			mode.SelectedIndex = 1; program.check("function map retains its existing default limit", Equals(view_limit.GetValue(control), 500));
			mode.SelectedIndex = 2; program.check("CFG retains its existing default limit", Equals(view_limit.GetValue(control), 250));
			mode.SelectedIndex = 0; program.check("switching modes restores the fixed call block limit", Equals(view_limit.GetValue(control), 120));
			program.check("depth GUI has exactly values one through eight, without Max", selector.Items.Cast<int>().SequenceEqual(Enumerable.Range(1, 8)) && Equals(selector.SelectedItem, 5));
			selector.SelectedItem = 3; program.check("choosing GUI depth saves it to dnSpy settings", new call_graph_settings(service).graph_depth == 3);
			mode.SelectedIndex = 2; program.check("method-call depth is disabled for intra-method CFG", !selector.IsEnabled);
			mode.SelectedIndex = 0; program.check("switching view modes keeps the chosen depth", selector.IsEnabled && Equals(selector.SelectedItem, 3));
			settings.change_graph_depth(7); program.check("depth selection follows shared settings changes", Equals(selector.SelectedItem, 7));
			control.dispose_graph();
		}
	}
}
