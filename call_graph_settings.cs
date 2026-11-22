using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.CallGraph {
	readonly record struct graph_display_options(int lines, double width, bool wrap, bool highlight);
	readonly record struct graph_analysis_options(int depth, bool hide_system, bool hide_accessors, bool hide_constructors, bool current_namespace, bool current_type, bool require_body, bool current_assembly);
	readonly record struct graph_integration_options(bool clean_explorer);
	[Export, PartCreationPolicy(CreationPolicy.Shared)]
	sealed class call_graph_settings {
		static readonly Guid settings_guid = new Guid("D4DB30D1-A37A-4EB6-9D3D-219AC62EA8C4");
		readonly ISettingsService settings_service;

		public event EventHandler? changed;

		public int maximum_lines { get; private set; } = 0;
		public int graph_depth { get; private set; } = 8;
		public int effective_maximum_lines => this.maximum_lines <= 0 ? 20 : this.maximum_lines;
		public int read_maximum_lines(int method_count) => this.effective_maximum_lines;
		public double block_width => graph_layout.maximum_automatic_width;
		public bool wrap_long_lines { get; private set; } = true;
		public bool syntax_highlighting => true;
		public bool hide_system_methods { get; private set; }
		public bool only_current_assembly { get; private set; }
		public bool hide_accessors { get; private set; }
		public bool hide_constructors { get; private set; }
		public bool only_current_namespace { get; private set; }
		public bool only_current_type { get; private set; }
		public bool require_method_body { get; private set; }
		public int minimum_call_site_count => 1;
		public bool clean_assembly_explorer_on_launch { get; private set; }
		public graph_display_options display_options => new(this.maximum_lines, this.block_width, this.wrap_long_lines, this.syntax_highlighting);
		public graph_analysis_options analysis_options => new(this.graph_depth, this.hide_system_methods, this.hide_accessors, this.hide_constructors, this.only_current_namespace, this.only_current_type, this.require_method_body, this.only_current_assembly);
		public graph_integration_options integration_options => new(this.clean_assembly_explorer_on_launch);

		[ImportingConstructor]
		public call_graph_settings(ISettingsService settings_service) {
			this.settings_service = settings_service;
			var section = settings_service.GetOrCreateSection(call_graph_settings.settings_guid);
			this.maximum_lines = call_graph_settings.normalize_lines(section.Attribute<int?>("MaximumLines") ?? this.maximum_lines);
			this.graph_depth = call_graph_settings.normalize_depth(section.Attribute<int?>("GraphDepth") ?? this.graph_depth);
			this.wrap_long_lines = section.Attribute<bool?>("WrapLongLines") ?? this.wrap_long_lines;
			this.hide_system_methods = section.Attribute<bool?>("HideSystemMethods") ?? this.hide_system_methods;
			this.only_current_assembly = section.Attribute<bool?>("OnlyCurrentAssembly") ?? this.only_current_assembly;
			this.hide_accessors = section.Attribute<bool?>("HideAccessors") ?? this.hide_accessors;
			this.hide_constructors = section.Attribute<bool?>("HideConstructors") ?? this.hide_constructors;
			this.only_current_namespace = section.Attribute<bool?>("OnlyCurrentNamespace") ?? this.only_current_namespace;
			this.only_current_type = section.Attribute<bool?>("OnlyCurrentType") ?? this.only_current_type;
			this.require_method_body = section.Attribute<bool?>("RequireMethodBody") ?? this.require_method_body;
			this.clean_assembly_explorer_on_launch = section.Attribute<bool?>("CleanAssemblyExplorerOnLaunch") ?? this.clean_assembly_explorer_on_launch;
		}

		public call_graph_filter_options create_filter_options() => new call_graph_filter_options(this.hide_system_methods,
			this.hide_accessors, this.hide_constructors, this.only_current_namespace, this.only_current_type, this.require_method_body, this.minimum_call_site_count, this.only_current_assembly);
		public void change_graph_depth(int depth) {
			depth = call_graph_settings.normalize_depth(depth);
			if (this.graph_depth == depth) return;
			this.graph_depth = depth;
			this.settings_service.GetOrCreateSection(call_graph_settings.settings_guid).Attribute("GraphDepth", this.graph_depth);
			this.changed?.Invoke(this, EventArgs.Empty);
		}

		public void apply(int maximum_lines, bool wrap_long_lines,
			bool hide_system_methods, bool hide_accessors, bool hide_constructors, bool only_current_namespace,
			bool only_current_type, bool require_method_body,
			bool clean_assembly_explorer_on_launch, bool only_current_assembly = false) {
			var previous = (display_options: this.display_options, analysis_options: this.analysis_options, integration_options: this.integration_options);
			this.only_current_assembly = only_current_assembly;
			this.maximum_lines = call_graph_settings.normalize_lines(maximum_lines);
			this.wrap_long_lines = wrap_long_lines;
			this.hide_system_methods = hide_system_methods;
			this.hide_accessors = hide_accessors;
			this.hide_constructors = hide_constructors;
			this.only_current_namespace = only_current_namespace;
			this.only_current_type = only_current_type;
			this.require_method_body = require_method_body;
			this.clean_assembly_explorer_on_launch = clean_assembly_explorer_on_launch;

			var section = this.settings_service.GetOrCreateSection(call_graph_settings.settings_guid);
			section.Attribute("MaximumLines", this.maximum_lines);
			section.Attribute("GraphDepth", this.graph_depth);
			section.Attribute("BlockWidth", this.block_width);
			section.Attribute("WrapLongLines", this.wrap_long_lines);
			section.Attribute("SyntaxHighlighting", this.syntax_highlighting);
			section.Attribute("HideSystemMethods", this.hide_system_methods);
			section.Attribute("OnlyCurrentAssembly", this.only_current_assembly);
			section.Attribute("HideAccessors", this.hide_accessors);
			section.Attribute("HideConstructors", this.hide_constructors);
			section.Attribute("OnlyCurrentNamespace", this.only_current_namespace);
			section.Attribute("OnlyCurrentType", this.only_current_type);
			section.Attribute("RequireMethodBody", this.require_method_body);
			section.Attribute("MinimumCallSiteCount", this.minimum_call_site_count);
			section.Attribute("CleanAssemblyExplorerOnLaunch", this.clean_assembly_explorer_on_launch);
			if (previous != (display_options: this.display_options, analysis_options: this.analysis_options, integration_options: this.integration_options)) this.changed?.Invoke(this, EventArgs.Empty);
		}

		static int normalize_lines(int value) => value <= 0 ? 0 : Math.Min(value, 500);
		static int normalize_depth(int value) => Math.Max(1, Math.Min(8, value));
	}

	[Export(typeof(IAppSettingsPageProvider))]
	sealed class call_graph_settings_page_provider : IAppSettingsPageProvider {
		readonly call_graph_settings settings;

		[ImportingConstructor]
		public call_graph_settings_page_provider(call_graph_settings settings) => this.settings = settings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new call_graph_settings_page(this.settings);
		}
	}

	sealed class call_graph_settings_page : AppSettingsPage {
		static readonly Guid page_guid = new Guid("1CDBA569-E3BD-43C9-87A7-9FD3AB93D370");
		readonly call_graph_settings settings;
		readonly ComboBox lines_combo_box;
		readonly CheckBox wrap_check_box;
		readonly CheckBox hide_system_methods_check_box;
		readonly CheckBox only_current_assembly_check_box;
		readonly CheckBox hide_accessors_check_box;
		readonly CheckBox hide_constructors_check_box;
		readonly CheckBox only_current_namespace_check_box;
		readonly CheckBox only_current_type_check_box;
		readonly CheckBox require_method_body_check_box;
		readonly CheckBox clean_explorer_check_box;

		public override Guid Guid => call_graph_settings_page.page_guid;
		public override double Order => 1800;
		public override string Title => ui_text.call_graph;
		public override object UIObject { get; }

		public call_graph_settings_page(call_graph_settings settings) {
			this.settings = settings;
			var panel = new StackPanel { Margin = new Thickness(16, 12, 20, 16), MaxWidth = 650 };
			panel.Children.Add(new TextBlock {
				Text = ui_text.settings_heading,
				FontSize = 16,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 12),
			});

			panel.Children.Add(call_graph_settings_page.label(ui_text.lines_title, ui_text.lines_description));
			this.lines_combo_box = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 12) };
			this.lines_combo_box.Items.Add(new line_choice(0, ui_text.auto_lines));
			foreach (int value in new[] { 5, 10, 20, 40, 80, 160 })
				this.lines_combo_box.Items.Add(new line_choice(value, value.ToString(CultureInfo.InvariantCulture)));
			this.select_lines(settings.maximum_lines);
			panel.Children.Add(this.lines_combo_box);

			this.wrap_check_box = new CheckBox { Content = ui_text.wrap_lines, IsChecked = settings.wrap_long_lines, Margin = new Thickness(0, 3, 0, 7) };
			panel.Children.Add(this.wrap_check_box);

			panel.Children.Add(call_graph_settings_page.section_title(ui_text.graph_filters));
			panel.Children.Add(new TextBlock {
				Text = ui_text.graph_filters_description,
				Opacity = 0.72,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 8),
			});
			this.hide_system_methods_check_box = call_graph_settings_page.filter_check_box(ui_text.hide_system_methods, settings.hide_system_methods);
			this.only_current_assembly_check_box = call_graph_settings_page.filter_check_box(ui_text.only_current_assembly, settings.only_current_assembly);
			this.only_current_assembly_check_box.ToolTip = ui_text.only_current_assembly_description;
			panel.Children.Add(this.only_current_assembly_check_box);
			this.hide_accessors_check_box = call_graph_settings_page.filter_check_box(ui_text.hide_accessors, settings.hide_accessors);
			this.hide_constructors_check_box = call_graph_settings_page.filter_check_box(ui_text.hide_constructors, settings.hide_constructors);
			this.only_current_namespace_check_box = call_graph_settings_page.filter_check_box(ui_text.only_current_namespace, settings.only_current_namespace);
			this.only_current_type_check_box = call_graph_settings_page.filter_check_box(ui_text.only_current_type, settings.only_current_type);
			this.require_method_body_check_box = call_graph_settings_page.filter_check_box(ui_text.require_method_body, settings.require_method_body);
			panel.Children.Add(this.hide_system_methods_check_box);
			panel.Children.Add(this.hide_accessors_check_box);
			panel.Children.Add(this.hide_constructors_check_box);
			panel.Children.Add(this.only_current_namespace_check_box);
			panel.Children.Add(this.only_current_type_check_box);
			panel.Children.Add(this.require_method_body_check_box);

			panel.Children.Add(call_graph_settings_page.section_title(ui_text.assembly_explorer));
			this.clean_explorer_check_box = new CheckBox {
				Content = ui_text.clean_explorer,
				IsChecked = settings.clean_assembly_explorer_on_launch,
				Margin = new Thickness(0, 0, 0, 5),
			};
			panel.Children.Add(this.clean_explorer_check_box);
			panel.Children.Add(new TextBlock {
				Text = ui_text.clean_explorer_description,
				Opacity = 0.72,
				TextWrapping = TextWrapping.Wrap,
			});

			UIObject = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		}

		public override void OnApply() {
			var line_choice = this.lines_combo_box.SelectedItem as global::dnSpy.CallGraph.call_graph_settings_page.line_choice;
			this.settings.apply(line_choice?.value ?? 0,
				this.wrap_check_box.IsChecked == true,
				this.hide_system_methods_check_box.IsChecked == true, this.hide_accessors_check_box.IsChecked == true,
				this.hide_constructors_check_box.IsChecked == true, this.only_current_namespace_check_box.IsChecked == true,
				this.only_current_type_check_box.IsChecked == true, this.require_method_body_check_box.IsChecked == true,
				this.clean_explorer_check_box.IsChecked == true, this.only_current_assembly_check_box.IsChecked == true);
		}

		void select_lines(int value) {
			foreach (line_choice choice in this.lines_combo_box.Items) {
				if (choice.value == value) {
					this.lines_combo_box.SelectedItem = choice;
					return;
				}
			}
			var custom = new line_choice(value, value.ToString(CultureInfo.InvariantCulture)); this.lines_combo_box.Items.Add(custom); this.lines_combo_box.SelectedItem = custom;
		}

		static FrameworkElement label(string title, string description) {
			var panel = new StackPanel();
			panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
			panel.Children.Add(new TextBlock { Text = description, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
			return panel;
		}

		static TextBlock section_title(string text) => new TextBlock {
			Text = text,
			FontSize = 16,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 4, 0, 10),
		};

		static CheckBox filter_check_box(string text, bool is_checked) => new CheckBox {
			Content = text,
			IsChecked = is_checked,
			Margin = new Thickness(0, 0, 0, 6),
		};

		sealed class line_choice {
			public int value { get; }
			readonly string text;
			public line_choice(int value, string text) { this.value = value; this.text = text; }
			public override string ToString() => this.text;
		}

	}
}
