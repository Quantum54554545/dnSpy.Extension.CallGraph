using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph {
	partial class call_graph_control : UserControl {
		static readonly ConditionalWeakTable<IDecompiler, SemaphoreSlim> decompiler_gates = new();
		enum graph_view_mode { call_blocks, function_map, control_flow }

		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly call_graph_settings settings;
		readonly IThemeService theme_service;
		readonly call_graph_index_cache index_cache;
		readonly bounded_snippet_cache<snippet_cache_key> snippet_cache = new(512, 2000000);
		readonly List<MethodDef> history = new List<MethodDef>();
		readonly List<MethodDef> visible_methods = new List<MethodDef>();
		readonly List<MethodDef> search_matches = new List<MethodDef>();
		readonly DispatcherTimer document_change_timer;
		CancellationTokenSource? operation_cancellation;
		CancellationTokenSource? preview_cancellation;
		MethodDef? current_root;
		call_graph_index? current_index;
		bool current_index_covers_all_loaded_modules;
		int history_position = -1;
		bool suppress_configuration_changed;
		const int call_block_limit = 120;
		const int function_map_limit = 500;
		const int cfg_block_limit = 250;
		int search_position = -1;
		bool disposed;
		bool visible_snippets_enabled;
		bool visible_snippets_running;
		bool visible_snippets_requested;
		bool analyzing_graph;
		bool can_continue_refinement;
		bool workspace_update_pending;
		HashSet<ModuleDef>? graph_modules;
		HashSet<ModuleDef> observed_modules = new HashSet<ModuleDef>();
		int current_preview_lines = 20;
		graph_display_options applied_display;
		graph_analysis_options applied_analysis;
		internal MethodDef? current_root_value => this.current_root;
		bool is_current(CancellationToken token) => !this.disposed && !token.IsCancellationRequested &&
			this.operation_cancellation is not null && this.operation_cancellation.Token == token;

		void cancel_current_work() {
			this.view_cancellation?.Cancel();
			this.graph_surface.cancel_pending_render();
			this.function_map_surface.cancel_pending_render();
			this.operation_cancellation?.Cancel();
			this.preview_cancellation?.Cancel();
			this.visible_snippets_enabled = false;
			this.visible_snippets_requested = false;
			this.analyzing_graph = false;
		}

		readonly struct snippet_cache_key : IEquatable<snippet_cache_key> {
			public readonly MethodDef definition;
			public readonly Guid language;
			public readonly int maximum_lines;

			public snippet_cache_key(MethodDef definition, Guid language, int maximum_lines) {
				this.definition = definition;
				this.language = language;
				this.maximum_lines = maximum_lines;
			}

			public bool Equals(snippet_cache_key other) => ReferenceEquals(this.definition, other.definition) &&
				this.language == other.language && this.maximum_lines == other.maximum_lines;
			public override bool Equals(object? obj) => obj is snippet_cache_key other && Equals(other);
			public override int GetHashCode() => ((this.definition.GetHashCode() * 397) ^ this.language.GetHashCode()) * 397 ^ this.maximum_lines;
		}

		graph_view_mode current_view_mode => (graph_view_mode)Math.Max(0, this.view_mode_combo_box.SelectedIndex);
		int current_view_limit => this.current_view_mode switch {
			graph_view_mode.function_map => call_graph_control.function_map_limit,
			graph_view_mode.control_flow => call_graph_control.cfg_block_limit,
			_ => call_graph_control.call_block_limit,
		};

		internal call_graph_control(IDocumentTabService document_tab_service, IDecompilerService decompiler_service,
			call_graph_settings settings, IThemeService theme_service, call_graph_index_cache index_cache, research_store? research_store = null) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.applied_display = settings.display_options;
			this.applied_analysis = settings.analysis_options;
			this.theme_service = theme_service;
			this.index_cache = index_cache;
			this.research_store = research_store ?? global::dnSpy.CallGraph.Core.research_store.@default;
			InitializeComponent();
			CommandBindings.Add(new CommandBinding(call_graph_command_loader.open_command, (_, event_args) => { this.explore_shortcut(); event_args.Handled = true; }));
			this.apply_localized_text();
			this.graph_surface.set_theme_service(theme_service);
			this.function_map_surface.set_theme_service(theme_service);
			this.graph_surface.configure(settings);
			this.function_map_surface.configure(settings);
			this.initialize_toolbar();
			this.initialize_research();
			this.document_change_timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) {
				Interval = TimeSpan.FromMilliseconds(350),
			};
			this.document_change_timer.Tick += this.document_change_timer_tick;
			settings.changed += this.settings_changed;
			theme_service.ThemeChanged += this.theme_service_theme_changed;
			document_tab_service.DocumentModified += this.document_tab_service_document_modified;
			document_tab_service.DocumentCollectionChanged += this.document_tab_service_document_collection_changed;
			this.observed_modules = new HashSet<ModuleDef>(this.get_loaded_modules());
			this.show_no_selection_message();
		}

		void apply_localized_text() {
			this.back_button.ToolTip = ui_text.previous_root;
			this.forward_button.ToolTip = ui_text.next_root;
			this.load_selection_button.Content = ui_text.current_selection;
			this.load_selection_button.ToolTip = ui_text.build_current_selection;
			this.graph_label.Text = ui_text.graph;
			this.calls_label.Text = ui_text.calls;
			this.depth_label.Text = ui_text.depth;
			this.depth_combo_box.ToolTip = ui_text.depth_tip;
			this.code_label.Text = ui_text.code;
			this.center_button.Content = ui_text.center_root;
			this.center_button.ToolTip = ui_text.center_root_tip;
			this.continue_refinement_button.Content = ui_text.pick("Continue refinement", "Уточнить дальше");
			this.continue_refinement_button.ToolTip = ui_text.pick("Resume the saved analysis frontier in the background; view depth is unchanged", "Продолжить сохранённый анализ в фоне; глубина просмотра не меняется");
			this.stop_refinement_button.Content = ui_text.pick("Stop", "Стоп");
			this.view_mode_combo_box.ToolTip = ui_text.view_mode_tip;
			this.direction_combo_box.ToolTip = ui_text.direction_tip;
			this.language_combo_box.ToolTip = ui_text.language_tip;
			this.search_label.Text = ui_text.search;
			this.search_text_box.ToolTip = ui_text.search_method_tip;
			this.search_previous_button.ToolTip = ui_text.previous_match;
			this.search_next_button.ToolTip = ui_text.next_match;
			this.clear_search_button.ToolTip = ui_text.clear_search;
		}

		void settings_changed(object? sender, EventArgs e) {
			bool analysis_changed = this.applied_analysis != this.settings.analysis_options;
			bool display_changed = this.applied_display != this.settings.display_options;
			this.applied_analysis = this.settings.analysis_options; this.applied_display = this.settings.display_options;
			if (!analysis_changed && !display_changed) return;
			if (analysis_changed) this.reset_research();
			bool previous = this.suppress_configuration_changed;
			this.suppress_configuration_changed = true;
			this.depth_combo_box.SelectedItem = this.settings.graph_depth;
			this.suppress_configuration_changed = previous;
			this.graph_surface.configure(this.settings);
			this.function_map_surface.configure(this.settings);
			this.queue_render();
		}

		void theme_service_theme_changed(object? sender, ThemeChangedEventArgs e) => this.queue_render();

		void document_tab_service_document_modified(object? sender, DocumentModifiedEventArgs e) {
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.BeginInvoke(new Action(() => this.document_tab_service_document_modified(sender, e)));
				return;
			}
			if (this.disposed)
				return;
			var modules = e.Documents.GetModules<ModuleDef>().Distinct().ToArray();
			if (modules.Length == 0)
				return;
			this.index_cache.invalidate(modules);
			this.reset_research(true);
			var modified = new HashSet<ModuleDef>(modules);
			lock (this.snippet_cache) {
				foreach (var key in this.snippet_cache.keys.Where(key => key.definition.Module is ModuleDef module && modified.Contains(module)).ToArray())
					this.snippet_cache.remove(key);
			}
			bool affects_current_index = this.current_index?.modules_value.Any(modified.Contains) == true ||
				(this.current_root is not null && this.graph_modules?.Any(modified.Contains) == true) ||
				(this.current_root?.Module is ModuleDef root_module && modified.Contains(root_module));
			if (!affects_current_index)
				return;
			this.cancel_current_work();
			this.current_index = null;
			this.current_index_covers_all_loaded_modules = false;
			this.set_busy(true, ui_text.module_changed);
			this.document_change_timer.Stop();
			this.document_change_timer.Start();
		}

		void document_tab_service_document_collection_changed(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.BeginInvoke(new Action(() => this.document_tab_service_document_collection_changed(sender, e)));
				return;
			}
			if (this.disposed)
				return;
			var loaded_modules = new HashSet<ModuleDef>(this.get_loaded_modules());
			if (this.observed_modules.SetEquals(loaded_modules)) return;
			this.observed_modules = loaded_modules;
			this.reset_research(true);
			this.prune_history(loaded_modules);
			this.index_cache.remove_missing(loaded_modules);
			lock (this.snippet_cache) {
				foreach (var key in this.snippet_cache.keys.Where(key => key.definition.Module is not ModuleDef module || !loaded_modules.Contains(module)).ToArray())
					this.snippet_cache.remove(key);
			}
			if ((this.analyzing_graph || this.visible_snippets_running) && this.graph_modules is not null &&
				this.graph_modules.IsSubsetOf(loaded_modules) && this.current_root?.Module is ModuleDef current_module && loaded_modules.Contains(current_module)) {
				this.workspace_update_pending = true;
				return;
			}
			this.current_index = null;
			this.current_index_covers_all_loaded_modules = false;
			this.cancel_current_work();
			if (this.current_root?.Module is not ModuleDef root_module || !loaded_modules.Contains(root_module)) {
				this.current_root = null;
				this.show_no_selection_message();
				return;
			}
			this.set_busy(true, ui_text.workspace_changed);
			this.document_change_timer.Stop();
			this.document_change_timer.Start();
		}

		void document_change_timer_tick(object? sender, EventArgs e) {
			this.document_change_timer.Stop();
			this.queue_render();
		}

		void initialize_toolbar() {
			this.view_mode_combo_box.ItemsSource = new[] { ui_text.call_blocks, ui_text.function_map, "CFG" };
			this.view_mode_combo_box.SelectedIndex = 0;
			this.direction_combo_box.ItemsSource = new[] { ui_text.callees, ui_text.callers, ui_text.both };
			this.direction_combo_box.SelectedIndex = 0;
			this.depth_combo_box.ItemsSource = Enumerable.Range(1, 8).ToArray();
			this.depth_combo_box.SelectedItem = this.settings.graph_depth;
			this.language_combo_box.ItemsSource = new[] { "C#", "IL" };
			this.language_combo_box.SelectedIndex = 0;

			this.load_selection_button.Click += (_, __) => this.load_current_selection();
			this.back_button.Click += (_, __) => this.navigate_history(-1);
			this.forward_button.Click += (_, __) => this.navigate_history(+1);
			this.center_button.Click += (_, __) => this.center_current_view();
			this.continue_refinement_button.Click += (_, __) => this.queue_render(true);
			this.stop_refinement_button.Click += (_, __) => { this.research_cancellation?.Cancel(); this.cancel_current_work(); this.can_continue_refinement = true; this.set_busy(false, ui_text.pick("Stopped; IL preview retained. Continue refinement to retry", "Остановлено; IL-граф сохранён. Можно продолжить уточнение")); };
			this.view_mode_combo_box.SelectionChanged += this.view_mode_changed;
			this.direction_combo_box.SelectionChanged += this.configuration_changed;
			this.depth_combo_box.SelectionChanged += (_, __) => { if (!this.suppress_configuration_changed && this.depth_combo_box.SelectedItem is int depth) this.settings.change_graph_depth(depth); };
			this.language_combo_box.SelectionChanged += this.configuration_changed;
			this.search_text_box.TextChanged += (_, __) => this.update_search_results(true);
			this.search_text_box.KeyDown += this.search_text_box_key_down;
			this.search_previous_button.Click += (_, __) => this.move_search(-1);
			this.search_next_button.Click += (_, __) => this.move_search(+1);
			this.clear_search_button.Click += (_, __) => this.search_text_box.Clear();

			this.graph_surface.navigate_requested += (_, e) => this.document_tab_service.FollowReference(e.reference, newTab: true);
			this.graph_surface.root_requested += (_, e) => { if (call_graph_control.resolve_method(e.reference) is MethodDef method) this.show_method(method); };
			this.graph_surface.selection_changed += (_, e) => this.status_text.Text = call_graph_control.format_reference_status(e.reference);
			this.graph_surface.visible_methods_changed += (_, __) => this.request_visible_snippets();
			this.function_map_surface.navigate_requested += (_, e) => this.document_tab_service.FollowReference(e.reference, newTab: true);
			this.function_map_surface.root_requested += (_, e) => { if (e.reference is MethodDef method) this.show_method(method); };
			this.function_map_surface.selection_changed += (_, e) => this.status_text.Text = call_graph_control.format_reference_status(e.reference);
			this.function_map_surface.code_preview_requested += (_, e) => this.request_function_map_preview(e.reference as MethodDef);
			this.update_mode_controls();
		}

		void search_text_box_key_down(object sender, KeyEventArgs e) {
			if (e.Key != Key.Enter)
				return;
			this.move_search((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
			e.Handled = true;
		}

		void set_visible_methods(IEnumerable<MethodDef> methods) {
			this.visible_methods.Clear();
			this.visible_methods.AddRange(methods.Distinct());
			this.update_search_results();
		}

		void update_search_results(bool focus = false) {
			var previous_match = this.search_position >= 0 && this.search_position < this.search_matches.Count ? this.search_matches[this.search_position] : null;
			this.search_matches.Clear();
			this.search_position = -1;
			string query = this.search_text_box.Text.Trim();
			if (query.Length != 0 && this.current_view_mode != graph_view_mode.control_flow) {
				this.search_matches.AddRange(this.visible_methods.Where(method =>
					call_graph_index.display_name(method).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
					call_graph_index.signature(method).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0));
				if (this.search_matches.Count != 0)
					this.search_position = Math.Max(0, this.search_matches.IndexOf(previous_match!));
			}
			bool has_query = query.Length != 0;
			this.search_previous_button.IsEnabled = this.search_next_button.IsEnabled = this.search_matches.Count != 0;
			this.clear_search_button.IsEnabled = has_query;
			this.search_result_text.Text = has_query ? ui_text.search_results(this.search_position + 1, this.search_matches.Count) : string.Empty;
			if (focus && this.search_position >= 0)
				this.focus_search_result();
		}

		void move_search(int delta) {
			if (this.search_matches.Count == 0)
				return;
			this.search_position = (this.search_position + delta + this.search_matches.Count) % this.search_matches.Count;
			this.search_result_text.Text = ui_text.search_results(this.search_position + 1, this.search_matches.Count);
			this.focus_search_result();
		}

		void focus_search_result() {
			if (this.search_position < 0 || this.search_position >= this.search_matches.Count)
				return;
			var method = this.search_matches[this.search_position];
			if (this.current_view_mode == graph_view_mode.function_map)
				this.function_map_surface.focus_method(method);
			else
				this.graph_surface.focus_reference(method);
		}

		void view_mode_changed(object sender, SelectionChangedEventArgs e) {
			if (this.suppress_configuration_changed)
				return;
			this.reset_research();
			this.update_mode_controls();
			this.queue_render();
		}

		void configuration_changed(object sender, SelectionChangedEventArgs e) {
			if (this.suppress_configuration_changed)
				return;
			if (ReferenceEquals(sender, this.direction_combo_box)) this.reset_research();
			this.queue_render();
		}

		void update_mode_controls() {
			var mode = this.current_view_mode;
			bool is_cfg = mode == graph_view_mode.control_flow;
			this.direction_combo_box.IsEnabled = !is_cfg;
			this.depth_combo_box.IsEnabled = !is_cfg;
			this.language_combo_box.IsEnabled = !is_cfg;
			this.search_text_box.IsEnabled = !is_cfg;
			if (is_cfg)
				this.search_text_box.ToolTip = ui_text.search_unavailable_in_cfg;
			else
				this.search_text_box.ToolTip = ui_text.search_method_tip;
			this.graph_surface.Visibility = mode == graph_view_mode.function_map ? Visibility.Collapsed : Visibility.Visible;
			this.function_map_surface.Visibility = mode == graph_view_mode.function_map ? Visibility.Visible : Visibility.Collapsed;
			this.update_search_results();
		}

		void center_current_view() {
			if (this.current_view_mode == graph_view_mode.function_map)
				this.function_map_surface.center_root();
			else
				this.graph_surface.center_root();
		}

		static string format_reference_status(object reference) {
			if (reference is MethodDef method)
				return call_graph_index.signature(method) + $"   token 0x{method.MDToken.Raw:X8}";
			if (reference is control_flow_block block)
				return $"{call_graph_index.signature(block.method)}   •   {block.title}–IL_{block.end_offset:X4}   •   {block.description}";
			return reference.ToString() ?? string.Empty;
		}

		public void focus_primary_control() {
			if (this.current_view_mode == graph_view_mode.function_map)
				this.function_map_surface.Focus();
			else
				this.graph_surface.Focus();
		}

		public void load_current_selection() {
			var method = call_graph_control.get_current_method(this.document_tab_service);
			if (method is null) {
				this.show_no_selection_message();
				return;
			}
			this.show_method(method);
		}
		internal void explore_shortcut() {
			if (this.disposed) return;
			var method = this.current_view_mode == graph_view_mode.function_map ? this.function_map_surface.shortcut_method : this.graph_surface.shortcut_method;
			if (method is not null) this.show_method(method);
		}

		internal void show_no_selection_message() {
			this.reset_research();
			this.cancel_current_work();
			this.document_change_timer.Stop();
			this.current_root = null;
			this.current_index = null;
			this.current_index_covers_all_loaded_modules = false;
			this.analyzing_graph = false; this.workspace_update_pending = false; this.graph_modules = null;
			this.set_busy(false, null);
			this.graph_surface.clear(ui_text.no_selection);
			this.function_map_surface.clear();
			this.status_text.Text = ui_text.no_selection_status;
			this.set_visible_methods(Array.Empty<MethodDef>());
		}

		public void show_method(MethodDef method) {
			if (method.Module is null) {
				this.status_text.Text = ui_text.module_missing;
				return;
			}
			this.reset_research();
			this.current_root = method;
			if (this.current_index is not null && !this.current_index.contains(method)) {
				this.current_index = null;
				this.current_index_covers_all_loaded_modules = false;
			}
			this.add_history(method);
			this.queue_render();
		}

		void queue_render(bool resume_refinement = false) {
			if (this.disposed || this.current_root is null)
				return;
			this.document_change_timer.Stop();
			this.research_cancellation?.Cancel();
			this.cancel_current_work();
			this.operation_cancellation?.Dispose();
			this.operation_cancellation = new CancellationTokenSource();
			_ = this.render_current_async(this.operation_cancellation.Token, resume_refinement);
		}

		async Task render_current_async(CancellationToken cancellation_token, bool resume_refinement = false) {
			var root = this.current_root;
			if (root?.Module is null)
				return;
			var mode = this.current_view_mode;
			this.analyzing_graph = true;
			int view_limit = this.current_view_limit;
			try {
				if (mode == graph_view_mode.control_flow) {
					this.set_visible_methods(Array.Empty<MethodDef>());
					this.set_busy(true, ui_text.building_cfg);
					int block_limit = view_limit;
					var cfg = await Task.Run(() => control_flow_graph.build(root, block_limit, cancellation_token), cancellation_token);
					cancellation_token.ThrowIfCancellationRequested();
					if (!this.is_current(cancellation_token)) return;
					this.graph_surface.render(cfg, this.settings.effective_maximum_lines);
					if (cfg.blocks.Count == 0) { this.status_text.Text = ui_text.no_method_body; return; }
					this.status_text.Text = ui_text.cfg_status(call_graph_index.signature(root), cfg.blocks.Count, cfg.edges.Count,
						cfg.is_truncated ? block_limit : null);
					return;
				}

				var direction = (call_graph_direction)Math.Max(0, this.direction_combo_box.SelectedIndex);
				int depth = this.depth_combo_box.SelectedItem is int selected_depth ? selected_depth : this.settings.graph_depth;
				int node_limit = view_limit;
				var configured = this.settings.create_filter_options();
				var filters = new call_graph_filter_options(configured.hide_system_methods, configured.hide_accessors, configured.hide_constructors,
					configured.only_current_namespace, configured.only_current_type, configured.require_method_body, configured.minimum_call_site_count,
					configured.only_current_assembly);
				var modules = this.get_loaded_modules();
				if (!modules.Contains(root.Module))
					modules.Insert(0, root.Module);
				this.graph_modules = new HashSet<ModuleDef>(modules);
				this.set_busy(true, ui_text.pick("Reading IL references…", "Чтение ссылок из IL…"));
				var workspace = this.index_cache.get_workspace(modules);
				IProgress<call_graph_slice> progress = new Progress<call_graph_slice>(preview => {
					if (!this.is_current(cancellation_token) || !this.analyzing_graph || !ReferenceEquals(root, this.current_root) || mode != this.current_view_mode) return;
					this.status_text.Text = ui_text.pick("IL references ready; resolving indirect targets…", "IL-ссылки готовы; разрешение косвенных целей…");
					if (this.exploration is not null) return;
					if (mode == graph_view_mode.function_map) this.function_map_surface.render(preview);
					else this.graph_surface.render(preview, this.build_snippet_preview(preview, this.language_combo_box.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP, out _));
				});
				await workspace.gate.WaitAsync(cancellation_token);
				(call_graph_index index, call_graph_slice slice) result;
				try {
					result = await Task.Run(() => {
						if (direction == call_graph_direction.callees)
							return workspace.browse(root, node_limit, filters, cancellation_token, progress, depth, resume_refinement);
						var combined = this.index_cache.get_or_build_combined(modules, workspace, cancellation_token);
						progress.Report(combined.select(root, direction, depth, node_limit, filters, cancellation_token, workspace, true));
						var refined = workspace.refine_callers(combined, root, cancellation_token, resume_refinement);
						var forward = direction == call_graph_direction.both ? workspace.browse(root, node_limit, filters, cancellation_token, depth: depth, resume_refinement: resume_refinement).slice : null;
						return (refined, refined.select(root, direction, depth, node_limit, filters, cancellation_token, workspace, false, forward));
					}, cancellation_token);
				}
				finally { workspace.gate.Release(); }
				cancellation_token.ThrowIfCancellationRequested();
				if (!this.is_current(cancellation_token)) return;
				var index = result.index;
				var slice = await this.prepare_exploration(result.slice, direction, node_limit, filters, cancellation_token);
				if (!this.is_current(cancellation_token)) return;
				this.can_continue_refinement = slice.can_continue_refinement;
				this.current_index = index;
				this.current_index_covers_all_loaded_modules = direction != call_graph_direction.callees;
				this.status_text.ToolTip = analysis_details.status_tool_tip(slice);
				if (mode == graph_view_mode.function_map) {
					this.function_map_surface.render(slice, index);
					this.set_visible_methods(slice.nodes.Select(node => node.method));
					this.status_text.Text = call_graph_control.format_graph_status(ui_text.function_map + ": " + call_graph_index.display_name(root), index, slice, filters, depth);
					return;
				}

				Guid language_guid = this.language_combo_box.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP;
				var decompiler = this.find_decompiler(language_guid);
				if (decompiler is null) {
					this.status_text.Text = ui_text.decompiler_unavailable(language_guid == DecompilerConstants.LANGUAGE_IL ? "IL" : "C#");
					return;
				}

				this.current_preview_lines = this.settings.read_maximum_lines(slice.nodes.Count);
				var snippets = this.build_snippet_preview(slice, decompiler.GenericGuid, out _);
				this.graph_surface.render(slice, snippets);
				this.visible_snippets_enabled = true;
				this.set_visible_methods(slice.nodes.Select(node => node.method));
				this.status_text.Text = call_graph_control.format_graph_status(call_graph_index.display_name(root), index, slice, filters, depth);
				this.request_visible_snippets();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				if (!this.is_current(cancellation_token)) return;
				if (this.current_view_mode == graph_view_mode.function_map)
					this.function_map_surface.clear();
				else
					this.graph_surface.clear(ui_text.render_failure(ex.Message));
				this.status_text.Text = ui_text.graph_error(ex.Message);
			}
			finally {
				if (this.is_current(cancellation_token)) {
					this.analyzing_graph = false;
					this.set_busy(false, null);
					this.flush_workspace_update();
				}
			}
		}

		void flush_workspace_update() {
			if (!this.workspace_update_pending || this.analyzing_graph || this.visible_snippets_running || this.disposed) return;
			this.workspace_update_pending = false;
			if (this.graph_modules is not null && this.graph_modules.SetEquals(this.get_loaded_modules())) return;
			this.current_index = null; this.current_index_covers_all_loaded_modules = false;
			this.document_change_timer.Stop(); this.document_change_timer.Start();
		}

		static string format_graph_status(string title, call_graph_index index, call_graph_slice slice, call_graph_filter_options filters, int depth) =>
			(index.is_reachable_index || slice.is_structural ? ui_text.pick($"IL + resolved targets: {slice.nodes.Count} methods, {slice.links.Count} links • initial depth {depth}; + expands in place", $"IL + разрешённые цели: методов {slice.nodes.Count}, связей {slice.links.Count} • начальная глубина {depth}; «+» раскрывает здесь") :
			ui_text.indexed_graph_status(title, slice.nodes.Count, slice.links.Count, index.module_count, index.method_count, index.link_count, slice.is_truncated)) +
			ui_text.unresolved_calls(slice.unresolved_indirect_call_count >= 0 ? slice.unresolved_indirect_call_count : slice.nodes.Sum(node => index.unresolved_calli_sites(node.method).Count)) + analysis_details.status(slice) +
			(filters.only_current_assembly ? ui_text.pick("   •   external calls shown without traversal", "   •   внешние вызовы показаны без обхода") : string.Empty);

		async void request_visible_snippets() {
			if (this.disposed || !this.visible_snippets_enabled || this.current_view_mode != graph_view_mode.call_blocks) return;
			this.visible_snippets_requested = true;
			if (this.visible_snippets_running) return;
			this.visible_snippets_running = true;
			this.visible_snippets_requested = false;
			var token = this.operation_cancellation?.Token ?? CancellationToken.None;
			try {
				var decompiler = this.find_decompiler(this.language_combo_box.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP);
				if (decompiler is null) return;
				int maximum_lines = this.current_preview_lines;
				var decompilation_context = new DecompilationContext { AsyncMethodBodyDecompilation = false };
				while (this.visible_snippets_enabled && this.current_view_mode == graph_view_mode.call_blocks) {
					token.ThrowIfCancellationRequested();
					this.visible_snippets_requested = false;
					var visible = this.graph_surface.visible_methods();
					MethodDef[] missing;
					lock (this.snippet_cache)
						missing = visible.Where(method => !this.snippet_cache.contains_key(new snippet_cache_key(method, decompiler.GenericGuid, maximum_lines))).Take(4).ToArray();
					if (missing.Length == 0) break;
					var snippets = await Task.Run(() => {
						var result = new Dictionary<MethodDef, highlighted_snippet>();
						var batch_time = Stopwatch.StartNew();
						foreach (var method in missing) {
							token.ThrowIfCancellationRequested();
							result[method] = call_graph_control.decompile_with_context(method, decompiler, maximum_lines, token, decompilation_context);
							if (batch_time.ElapsedMilliseconds >= 60) break;
						}
						return result;
					}, token);
					token.ThrowIfCancellationRequested();
					if (!this.is_current(token)) return;
					lock (this.snippet_cache)
						foreach (var pair in snippets) this.snippet_cache[new snippet_cache_key(pair.Key, decompiler.GenericGuid, maximum_lines)] = pair.Value;
					this.graph_surface.update_snippets(snippets);
					await Dispatcher.Yield(DispatcherPriority.Background);
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				if (this.is_current(token)) {
					this.visible_snippets_enabled = this.visible_snippets_requested = false;
					this.status_text.Text = ui_text.graph_error(ex.Message);
				}
			}
			finally {
				this.visible_snippets_running = false;
				this.flush_workspace_update();
				if (this.visible_snippets_requested && !this.disposed && this.visible_snippets_enabled)
					_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(this.request_visible_snippets));
			}
		}

		Dictionary<MethodDef, highlighted_snippet> build_snippet_preview(call_graph_slice slice, Guid language,
			out bool complete) {
			var result = new Dictionary<MethodDef, highlighted_snippet>();
			complete = true;
			int maximum_lines = this.current_preview_lines;
			lock (this.snippet_cache) {
				foreach (var node in slice.nodes) {
					var key = new snippet_cache_key(node.method, language, maximum_lines);
					if (this.snippet_cache.try_get_value(key, out var cached))
						result[node.method] = cached.limit_lines(maximum_lines);
					else {
						complete = false;
						result[node.method] = highlighted_snippet.plain(ui_text.loading_code,
							dnSpy.Contracts.Text.TextColor.Gray);
					}
				}
			}
			return result;
		}

		static highlighted_snippet decompile(MethodDef method, IDecompiler decompiler, int maximum_lines,
			CancellationToken cancellation_token) => call_graph_control.decompile_with_context(method, decompiler, maximum_lines, cancellation_token,
				new DecompilationContext { AsyncMethodBodyDecompilation = false });

		static highlighted_snippet decompile_with_context(MethodDef method, IDecompiler decompiler, int maximum_lines,
			CancellationToken cancellation_token, DecompilationContext context, int maximum_characters = 16000, int timeout_ms = 900) {
			var output = new highlighted_decompiler_output(maximum_lines, maximum_characters);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation_token);
			timeout.CancelAfter(TimeSpan.FromMilliseconds(timeout_ms));
			try {
				context.CancellationToken = timeout.Token;
				try {
					var gate = call_graph_control.decompiler_gates.GetValue(decompiler, _ => new SemaphoreSlim(1, 1));
					gate.Wait(timeout.Token);
					try {
						timeout.Token.ThrowIfCancellationRequested();
						decompiler.Decompile(method, output, context);
					}
					finally { gate.Release(); }
				}
				catch (snippet_limit_reached_exception) { }
				return output.create_snippet();
			}
			catch (OperationCanceledException) when (!cancellation_token.IsCancellationRequested) {
				return control_flow_graph.method_preview(method, maximum_lines);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) {
				return highlighted_snippet.plain(ui_text.pick("Decompilation failed: ", "Ошибка декомпиляции: ") + ex.Message,
					dnSpy.Contracts.Text.TextColor.Error);
			}
		}

		async void request_function_map_preview(MethodDef? method) {
			if (method is null || this.disposed || this.current_view_mode != graph_view_mode.function_map)
				return;
			this.preview_cancellation?.Cancel();
			this.preview_cancellation?.Dispose();
			this.preview_cancellation = new CancellationTokenSource();
			var cancellation_token = this.preview_cancellation.Token;
			try {
				Guid language_guid = this.language_combo_box.SelectedIndex == 1
					? DecompilerConstants.LANGUAGE_IL
					: DecompilerConstants.LANGUAGE_CSHARP;
				var decompiler = this.find_decompiler(language_guid);
				if (decompiler is null)
					return;
				int preview_lines = Math.Min(24, this.settings.effective_maximum_lines);
				var key = new snippet_cache_key(method, decompiler.GenericGuid, preview_lines);
				highlighted_snippet? snippet;
				lock (this.snippet_cache) {
					this.snippet_cache.try_get_value(key, out snippet);
					if (snippet is null)
						snippet = this.snippet_cache
							.Where(pair => ReferenceEquals(pair.Key.definition, method) && pair.Key.language == decompiler.GenericGuid)
							.Select(pair => pair.Value.limit_lines(preview_lines))
							.FirstOrDefault();
				}
				if (snippet is null) {
					snippet = await Task.Run(() => call_graph_control.decompile(method, decompiler, preview_lines, cancellation_token), cancellation_token);
					cancellation_token.ThrowIfCancellationRequested();
					if (this.disposed) return;
					lock (this.snippet_cache)
						this.snippet_cache[key] = snippet;
				}
				cancellation_token.ThrowIfCancellationRequested();
				if (!this.disposed && this.current_view_mode == graph_view_mode.function_map)
					this.function_map_surface.set_code_preview(method, snippet);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				if (!this.disposed && !cancellation_token.IsCancellationRequested && this.current_view_mode == graph_view_mode.function_map)
					this.function_map_surface.set_code_preview(method, highlighted_snippet.plain(ex.Message, dnSpy.Contracts.Text.TextColor.Error));
			}
		}

		IDecompiler? find_decompiler(Guid generic_guid) => this.decompiler_service.AllDecompilers
			.Where(item => item.GenericGuid == generic_guid).OrderBy(item => item.OrderUI).FirstOrDefault();

		List<ModuleDef> get_loaded_modules() => this.document_tab_service.DocumentTreeView.DocumentService.GetDocuments()
			.GetModules<ModuleDef>()
			.Distinct()
			.OrderBy(module => module.Name?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
			.ToList();

		internal static MethodDef? get_current_method(IDocumentTabService document_tab_service) {
			var viewer = document_tab_service.ActiveTab?.TryGetDocumentViewer();
			var method = call_graph_control.resolve_method(viewer?.SelectedReference?.Data.Reference);
			if (method is not null)
				return method;
			foreach (var node in document_tab_service.ActiveTab?.Content.Nodes ?? Enumerable.Empty<DocumentTreeNodeData>()) {
				if (node is IMDTokenNode token_node && call_graph_control.resolve_method(token_node.Reference) is MethodDef tab_method)
					return tab_method;
			}
			foreach (var node in document_tab_service.DocumentTreeView.TreeView.TopLevelSelection) {
				if (node is IMDTokenNode token_node && call_graph_control.resolve_method(token_node.Reference) is MethodDef tree_method)
					return tree_method;
			}
			return null;
		}

		internal static MethodDef? resolve_method(object? reference) {
			if (reference is MethodDef definition)
				return definition;
			if (reference is IMethod method) {
				try { return method.ResolveMethodDef(); }
				catch (Exception) { return null; }
			}
			return null;
		}

		internal void dispose_graph() {
			if (this.disposed)
				return;
			this.disposed = true;
			this.graph_surface.save_positions(); this.research_store.save(); this.research_cancellation?.Cancel(); this.research_cancellation?.Dispose(); this.research_cancellation = null;
			this.view_cancellation?.Cancel(); this.view_cancellation?.Dispose(); this.view_cancellation = null;
			foreach (var window in this.research_windows.ToArray()) window.Close(); this.research_windows.Clear();
			this.graph_modules = null; this.workspace_update_pending = false;
			this.settings.changed -= this.settings_changed;
			this.theme_service.ThemeChanged -= this.theme_service_theme_changed;
			this.document_tab_service.DocumentModified -= this.document_tab_service_document_modified;
			this.document_tab_service.DocumentCollectionChanged -= this.document_tab_service_document_collection_changed;
			this.document_change_timer.Stop();
			this.document_change_timer.Tick -= this.document_change_timer_tick;
			this.operation_cancellation?.Cancel();
			this.operation_cancellation?.Dispose();
			this.operation_cancellation = null;
			this.preview_cancellation?.Cancel();
			this.preview_cancellation?.Dispose();
			this.preview_cancellation = null;
			lock (this.snippet_cache)
				this.snippet_cache.clear();
			this.history.Clear();
			this.visible_methods.Clear();
			this.search_matches.Clear();
			this.current_root = null;
			this.current_index = null;
			this.current_index_covers_all_loaded_modules = false;
			this.graph_surface.clear();
			this.function_map_surface.clear();
		}

		void add_history(MethodDef method) {
			if (this.history_position >= 0 && this.history_position < this.history.Count && ReferenceEquals(this.history[this.history_position], method))
				return;
			if (this.history_position + 1 < this.history.Count)
				this.history.RemoveRange(this.history_position + 1, this.history.Count - this.history_position - 1);
			this.history.Add(method);
			this.history_position = this.history.Count - 1;
			this.update_history_buttons();
		}

		void navigate_history(int delta) {
			this.prune_history(new HashSet<ModuleDef>(this.get_loaded_modules()));
			int next = this.history_position + delta;
			if (next < 0 || next >= this.history.Count)
				return;
			this.history_position = next;
			this.current_root = this.history[this.history_position];
			if (this.current_index is not null && !this.current_index.contains(this.current_root)) {
				this.current_index = null;
				this.current_index_covers_all_loaded_modules = false;
			}
			this.update_history_buttons();
			this.queue_render();
		}

		void update_history_buttons() {
			this.back_button.IsEnabled = this.history_position > 0;
			this.forward_button.IsEnabled = this.history_position >= 0 && this.history_position + 1 < this.history.Count;
		}

		void prune_history(ISet<ModuleDef> loaded_modules) {
			int position = -1;
			var retained = new List<MethodDef>();
			for (int index = 0; index < this.history.Count; index++) {
				if (this.history[index].Module is not ModuleDef module || !loaded_modules.Contains(module)) continue;
				retained.Add(this.history[index]);
				if (index <= this.history_position) position = retained.Count - 1;
			}
			this.history.Clear(); this.history.AddRange(retained); this.history_position = position;
			this.update_history_buttons();
		}

		void set_busy(bool busy, string? message) {
			this.busy_progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
			this.continue_refinement_button.IsEnabled = !busy && this.can_continue_refinement && this.current_view_mode != graph_view_mode.control_flow;
			this.stop_refinement_button.IsEnabled = busy;
			if (!string.IsNullOrEmpty(message))
				this.status_text.Text = message;
		}
	}
}
