using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.CallGraph {
	partial class call_graph_control {
		readonly research_store research_store;
		exploration_graph? exploration;
		call_graph_slice? research_slice;
		workspace_analysis? search_workspace;
		workspace_search? workspace_search;
		CancellationTokenSource? research_cancellation;
		CancellationTokenSource? view_cancellation;
		bool research_reading;
		int view_workers;
		string? research_save_error;
		readonly List<Window> research_windows = new();
		sealed record evidence_row(string title, string detail);
		void initialize_research() {
			this.research_button.Content = this.research_title.Text = ui_text.pick("Evidence", "Связи");
			this.cancel_research_button.Content = ui_text.pick("Cancel", "Отмена");
			this.graph_surface.set_research_store(this.research_store); this.graph_surface.is_pinned = method => this.exploration?.is_pinned(method) == true;
			this.graph_surface.research_requested += this.research_requested;
			this.function_map_surface.research_requested += this.research_requested;
			this.graph_surface.edge_requested += (_, event_args) => { if (event_args.reference is graph_link_presentation link) this.show_edge_evidence(link); };
			this.research_button.Click += (_, __) => { if (this.research_pane.Visibility == Visibility.Visible) this.close_research(); else if ((this.graph_surface.resolve_shortcut_method(null) ?? this.current_root) is MethodDef method) this.show_method_evidence(method); else this.open_research(); };
			this.close_research_button.Click += (_, __) => this.close_research();
			this.cancel_research_button.Click += (_, __) => { this.research_cancellation?.Cancel(); this.view_cancellation?.Cancel(); };
			this.call_sites_list.SelectionChanged += (_, __) => { if (this.call_sites_list.SelectedItem is evidence_row row) this.evidence_text_box.Text = row.detail; };
			if (this.research_store.last_error is not null) this.research_status.Text = ui_text.pick("Research file unavailable: ", "Файл исследования недоступен: ") + this.research_store.last_error;
			foreach (var control in new Control[] { this.call_sites_list, this.evidence_text_box }) {
				control.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow"); control.SetResourceReference(Control.ForegroundProperty, "SystemColorsWindowText"); control.SetResourceReference(Control.BorderBrushProperty, "CommonControlsTextBoxBorder");
			}
			this.research_splitter.SetResourceReference(Control.BackgroundProperty, "CommonControlsTextBoxBorder");
		}
		void open_research() { this.research_pane.Visibility = this.research_splitter.Visibility = Visibility.Visible; if (this.research_column.Width.Value < 300) this.research_column.Width = new GridLength(390); }
		void close_research() { this.research_pane.Visibility = this.research_splitter.Visibility = Visibility.Collapsed; this.research_column.Width = new GridLength(0); this.cancel_research_read(); }
		void cancel_research_read() { var previous = this.research_cancellation; this.research_cancellation = null; previous?.Cancel(); previous?.Dispose(); this.research_reading = false; this.update_research_activity(); }
		void update_research_activity() => this.cancel_research_button.Visibility = this.research_reading || this.view_workers != 0 ? Visibility.Visible : Visibility.Collapsed;
		CancellationToken begin_view() { this.view_cancellation?.Cancel(); this.view_cancellation?.Dispose(); this.view_cancellation = new CancellationTokenSource(); this.view_workers++; this.update_research_activity(); return this.view_cancellation.Token; }
		void end_view() { this.view_workers--; this.update_research_activity(); }
		void reset_research(bool workspace_changed = false) {
			this.graph_surface.save_positions(); this.exploration = null; this.research_slice = null;
			this.view_cancellation?.Cancel(); this.cancel_research_read();
			if (workspace_changed) { this.workspace_search = null; this.search_workspace = null; foreach (var window in this.research_windows.ToArray()) window.Close(); this.research_windows.Clear(); }
		}
		async Task<call_graph_slice> prepare_exploration(call_graph_slice slice, call_graph_direction direction, int limit, call_graph_filter_options filters, CancellationToken token) {
			var original = this.exploration; var root = this.current_root!; var card_key = this.capture_card_keys();
			var prepared = await Task.Run(() => {
				var next = original is not null && original.root == root && original.direction == direction && original.limit == limit ? original.copy() : new exploration_graph(root, direction, limit, filters);
				next.merge(slice, true); return (graph: next, slice: next.project(card_key));
			}, token);
			token.ThrowIfCancellationRequested(); this.exploration = prepared.graph; return this.research_slice = prepared.slice;
		}
		Func<MethodDef, object> capture_card_keys() {
			var annotations = this.research_store.snapshot(); var groups = this.graph_surface.expanded_groups_snapshot(); bool call_blocks = this.current_view_mode == graph_view_mode.call_blocks;
			return method => { var group = annotations.GetValueOrDefault(research_store.key(method))?.group ?? ""; return call_blocks && group.Length != 0 && !groups.Contains(group) ? new research_group(group) : (object)method; };
		}
		async Task render_exploration() {
			if (this.exploration is null || this.disposed) return; var original = this.exploration; var card_key = this.capture_card_keys(); var token = this.begin_view();
			try {
				var prepared = await Task.Run(() => { var next = original.copy(); return (graph: next, slice: next.project(card_key)); }, token);
				if (this.disposed || token.IsCancellationRequested || original != this.exploration) return; this.exploration = prepared.graph; this.display_exploration(prepared.slice);
			}
			catch (OperationCanceledException) { }
			catch (Exception error) { this.research_status.Text = error.Message; }
			finally { this.end_view(); }
		}
		void display_exploration(call_graph_slice slice) {
			this.research_slice = slice;
			if (this.current_view_mode == graph_view_mode.function_map) this.function_map_surface.render(slice, this.current_index);
			else { this.graph_surface.render(slice, this.build_snippet_preview(slice, this.language_combo_box.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP, out _)); this.visible_snippets_enabled = true; this.request_visible_snippets(); }
			this.set_visible_methods(slice.nodes.Select(node => node.method));
			this.status_text.Text = ui_text.pick($"View: {slice.nodes.Count} methods · known: {this.exploration!.known_method_count} · root unchanged", $"Фрагмент: методов {slice.nodes.Count} · известно: {this.exploration!.known_method_count} · корень сохранён") + analysis_details.status(slice); this.status_text.ToolTip = analysis_details.status_tool_tip(slice);
		}
		async void research_requested(object? sender, research_action_event_args event_args) {
			if ((this.view_workers != 0 || this.analyzing_graph) && event_args.action is not (research_action.evidence or research_action.sources)) { this.status_text.Text = ui_text.pick("Wait for the current graph update or cancel it", "Дождись текущего обновления графа или отмени его"); return; }
			if (event_args.reference is research_group group) {
				if (event_args.action == research_action.expand_group) this.graph_surface.expand_group(group.name);
				else if (event_args.action == research_action.ungroup) { this.research_store.remove_group(group.name); this.save_research(); }
				await this.render_exploration(); return;
			}
			if (event_args.reference is not MethodDef method) return;
			switch (event_args.action) {
				case research_action.expand: await this.expand_research(method); break;
				case research_action.collapse: this.exploration?.collapse(method); await this.render_exploration(); break;
				case research_action.pin: if (this.exploration?.pin(method) == false) { this.status_text.Text = ui_text.pick("Unpin another node first", "Сначала открепи другой узел"); return; } await this.render_exploration(); break;
				case research_action.evidence: this.show_method_evidence(method); break;
				case research_action.sources: this.open_value_sources(method); break;
				case research_action.collapse_group: this.graph_surface.collapse_group(this.research_store.get(method).group); await this.render_exploration(); break;
				default:
					var annotation = this.research_store.get(method); string? value;
					if (event_args.action == research_action.compact) annotation = annotation with { compact = !annotation.compact };
					else if (event_args.action == research_action.alias) { value = research_dialogs.edit(this, ui_text.pick("Method alias (empty removes)", "Псевдоним метода (пусто — убрать)"), annotation.alias, false); if (value is null) return; annotation = annotation with { alias = value.Trim() }; }
					else if (event_args.action == research_action.note) { value = research_dialogs.edit(this, ui_text.pick("Method note", "Заметка к методу"), annotation.note, true); if (value is null) return; annotation = annotation with { note = value }; }
					else if (event_args.action == research_action.group) { if (method == this.current_root) { this.status_text.Text = ui_text.pick("The root stays outside groups", "Корень остаётся вне групп"); return; } value = research_dialogs.edit(this, ui_text.pick("Group name (empty removes)", "Имя группы (пусто — убрать)"), annotation.group, false); if (value is null) return; annotation = annotation with { group = value.Trim() }; }
					this.research_store.set(method, annotation); this.save_research(); await this.render_exploration(); break;
			}
		}
		void save_research() {
			this.graph_surface.save_positions(); this.research_save_error = this.research_store.save() ? null : ui_text.pick("Could not save research: ", "Не удалось сохранить исследование: ") + this.research_store.last_error;
			if (this.research_save_error is not null) { this.open_research(); this.research_status.Text = this.research_save_error; }
		}
		async Task expand_research(MethodDef method) {
			if (this.exploration is null || this.current_root is null) return;
			var session = this.exploration; var token = this.begin_view();
			this.status_text.Text = ui_text.pick("Expanding IL relations in the background…", "Раскрытие IL-связей в фоне…");
			try {
				var modules = this.get_research_modules(); if (!modules.Contains(method.Module)) { this.research_status.Text = ui_text.pick("Load the target assembly into dnSpy first", "Сначала загрузи сборку цели в dnSpy"); this.open_research(); return; }
				var workspace = this.index_cache.get_workspace(modules); var configured = this.settings.create_filter_options();
				await workspace.gate.WaitAsync(token); (call_graph_slice calls, call_graph_slice? incoming) direct;
				try { direct = await Task.Run(() => {
					if (session.direction == call_graph_direction.callers) {
						var index = this.index_cache.get_or_build_combined(modules, workspace, token);
						var refined = workspace.refine_callers(index, method, token);
						return (calls: refined.select(method, call_graph_direction.callers, 1, int.MaxValue, configured, token, workspace, true), incoming: (call_graph_slice?)null);
					}
					var forward = workspace.browse(method, session.limit, call_graph_filter_options.none, token, depth: 1, resume_refinement: false).slice;
					var full = workspace.structural_slice(method, 1, int.MaxValue, call_graph_filter_options.none, token, forward);
					if (session.direction == call_graph_direction.callees) return (calls: full, incoming: (call_graph_slice?)null);
					var combined = this.index_cache.get_or_build_combined(modules, workspace, token);
					var incoming = workspace.refine_callers(combined, method, token).select(method, session.direction, 1, int.MaxValue, configured, token, workspace, true, full);
					return (calls: full, incoming: (call_graph_slice?)incoming);
				}, token); }
				finally { workspace.gate.Release(); }
				if (this.disposed || token.IsCancellationRequested || !ReferenceEquals(session, this.exploration)) return;
				var card_key = this.capture_card_keys();
				var prepared = await Task.Run(() => { var next = session.copy(); next.merge(direct.calls); if (direct.incoming is not null) next.merge(direct.incoming); next.expand(method); return (graph: next, slice: next.project(card_key)); }, token);
				if (this.disposed || token.IsCancellationRequested || session != this.exploration) return; this.exploration = prepared.graph; this.display_exploration(prepared.slice);
				if (this.research_slice!.diagnostics_for(method).Any(note => note.kind == graph_diagnostic_kind.node_limit)) this.status_text.Text += ui_text.pick(" · Collapse a branch or group methods to free space", " · Сверни ветку или объедини методы в группу, чтобы освободить место");
			}
			catch (OperationCanceledException) { }
			catch (Exception error) { this.open_research(); this.research_status.Text = error.Message; }
			finally { this.end_view(); }
		}
		CancellationToken begin_research() {
			this.research_cancellation?.Cancel(); this.research_cancellation?.Dispose(); this.research_cancellation = new CancellationTokenSource();
			this.research_reading = true; this.update_research_activity(); this.research_status.Text = ui_text.pick("Reading workspace IL…", "Чтение IL загруженных сборок…"); return this.research_cancellation.Token;
		}
		void end_research(CancellationToken token) { if (this.research_cancellation?.Token == token) { this.research_reading = false; this.update_research_activity(); if (token.IsCancellationRequested) this.research_status.Text = ui_text.pick("Cancelled; previous results retained", "Отменено; предыдущие результаты сохранены"); if (this.research_save_error is not null) this.research_status.Text = this.research_save_error; } }
		List<ModuleDef> get_research_modules() { var modules = this.get_loaded_modules(); if (this.current_root?.Module is ModuleDef module && !modules.Contains(module)) modules.Insert(0, module); return modules; }
		async Task<(workspace_analysis workspace, workspace_search search)> get_research_index(CancellationToken token) {
			var workspace = this.index_cache.get_workspace(this.get_research_modules()); if (ReferenceEquals(workspace, this.search_workspace) && this.workspace_search is not null) return (workspace: workspace, search: this.workspace_search);
			await workspace.gate.WaitAsync(token);
			try { var result = await Task.Run(() => new workspace_search(workspace, token), token); token.ThrowIfCancellationRequested(); this.search_workspace = workspace; this.workspace_search = result; return (workspace: workspace, search: result); }
			finally { workspace.gate.Release(); }
		}
		static string describe_site(call_site_info site) => call_graph_index.signature(site.source) + $"\nIL_{site.offset:X4}: {site.operation} {site.declared_target}\n\n" + ui_text.pick("Targets:\n", "Цели:\n") + string.Join("\n", site.targets.Select(call_graph_index.signature)) + "\n\n" + string.Join("\n", site.evidence.Select(item => $"IL_{item.offset:X4}: {item.operation} [{item.kind}] {item.target?.FullName}\n{item.reason}")) + (site.uncertainty is null ? "" : "\n\n? " + site.uncertainty) + ui_text.pick("\n\nStatic evidence, not proof of execution.", "\n\nСтатические основания, не доказательство выполнения.");
		async void show_method_evidence(MethodDef method) {
			this.cancel_research_read();
			this.open_research(); this.evidence_heading.Text = call_graph_index.display_name(method);
			var snapshot = this.research_slice; this.populate_method_evidence(method, snapshot);
			if (!method.HasBody || snapshot?.nodes.Any(node => node.method == method && node.is_expanded) == true && this.exploration?.direction == call_graph_direction.callees) return;
			var token = this.begin_research();
			try {
				var workspace = this.index_cache.get_workspace(this.get_research_modules()); await workspace.gate.WaitAsync(token); call_graph_slice complete;
				try { complete = await Task.Run(() => workspace.structural_slice(method, 1, int.MaxValue, call_graph_filter_options.none, token, snapshot), token); } finally { workspace.gate.Release(); }
				if (!this.disposed && !token.IsCancellationRequested) this.populate_method_evidence(method, complete);
			}
			catch (OperationCanceledException) { }
			catch (Exception error) { this.research_status.Text = error.Message; }
			finally { this.end_research(token); }
		}
		void populate_method_evidence(MethodDef method, call_graph_slice? slice) {
			var rows = (slice?.call_sites.Where(site => site.source == method).OrderBy(site => site.offset).Select(site => new evidence_row($"IL_{site.offset:X4}: {site.operation} · {site.targets.Count}", call_graph_control.describe_site(site))) ?? Enumerable.Empty<evidence_row>()).Concat(slice?.diagnostics_for(method).Select(note => new evidence_row($"IL_{note.offset:X4}: {note.kind}", analysis_details.describe(note))) ?? Enumerable.Empty<evidence_row>()).ToArray(); this.call_sites_list.ItemsSource = rows;
			this.evidence_text_box.Text = slice is null ? "" : string.Join("\n", slice.diagnostics_for(method).Select(analysis_details.describe)); if (rows.Length != 0) this.call_sites_list.SelectedIndex = 0;
			this.research_status.Text = ui_text.pick("Select a site or click an edge. Evidence is independent of hidden cards.", "Выбери место вызова или нажми стрелку. Основания не зависят от скрытых карточек.");
		}
		void show_edge_evidence(graph_link_presentation link) { this.cancel_research_read(); this.open_research(); this.call_sites_list.ItemsSource = null; this.evidence_heading.Text = ui_text.pick("Relation: ", "Связь: ") + link.kind; this.evidence_text_box.Text = link.tool_tip; this.research_status.Text = ui_text.pick("Each offset is retained separately; & means an address, not invocation.", "Каждый offset сохранён отдельно; & означает адрес, а не вызов."); }
		async void open_value_sources(MethodDef method) {
			var token = this.begin_research();
			try {
				var index = await this.get_research_index(token); if (this.disposed || token.IsCancellationRequested) return;
				var window = new value_sources_window(method, index.workspace, index.search, this.theme_service, reference => this.document_tab_service.FollowReference(reference, newTab: true)) { Owner = Window.GetWindow(this) };
				this.research_windows.Add(window); window.Closed += (_, __) => this.research_windows.Remove(window); window.Show();
			}
			catch (OperationCanceledException) { }
			catch (Exception error) { this.open_research(); this.research_status.Text = error.Message; }
			finally { this.end_research(token); }
		}
	}
}
