using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	sealed partial class call_graph_surface {
		research_store? research_store;
		readonly HashSet<string> expanded_groups = new(StringComparer.Ordinal);
		internal Func<MethodDef, bool> is_pinned { get; set; } = _ => false;
		internal event EventHandler<research_action_event_args>? research_requested;
		internal event EventHandler<graph_reference_event_args>? edge_requested;
		internal void set_research_store(research_store store) => this.research_store = store;
		internal void configure_sources() { this.node_width = 300; this.automatic_width = false; this.wrap_long_lines = true; }
		internal void expand_group(string name) => this.expanded_groups.Add(name);
		internal void collapse_group(string name) => this.expanded_groups.Remove(name);
		internal bool is_group_expanded(string name) => this.expanded_groups.Contains(name);
		internal ISet<string> expanded_groups_snapshot() => new HashSet<string>(this.expanded_groups);
		internal void render(graph_presentation graph, IReadOnlyDictionary<object, highlighted_snippet> snippets) => this.render_core(graph, snippets, string.Empty, false);
		void request(object reference, research_action action) => this.research_requested?.Invoke(this, new research_action_event_args(reference, action));
		void update_source_footer(Grid grid, object reference, highlighted_snippet snippet) {
			foreach (var footer in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == 3).ToArray()) grid.Children.Remove(footer);
			if (reference is not MethodDef || !snippet.is_truncated) return;
			var button = new Button { Content = ui_text.open_full_source, HorizontalAlignment = HorizontalAlignment.Left, Height = 22, Margin = new Thickness(7, 0, 7, 3), Padding = new Thickness(5, 0, 5, 0) };
			button.Click += (_, __) => this.navigate_requested?.Invoke(this, new graph_reference_event_args(reference)); Grid.SetRow(button, 3); grid.Children.Add(button);
		}
		MenuItem action_item(object reference, research_action action, string title) {
			var item = new MenuItem { Header = title }; item.Click += (_, __) => this.request(reference, action); return item;
		}
		void add_research_menu(ContextMenu menu, object reference) {
			if (reference is research_group group) {
				menu.Items.Add(this.action_item(group, research_action.expand_group, ui_text.pick("Expand group", "Развернуть группу")));
				menu.Items.Add(this.action_item(group, research_action.ungroup, ui_text.pick("Remove grouping", "Убрать группировку"))); return;
			}
			if (reference is value_origin) {
				menu.Items.Add(this.action_item(reference, research_action.expand, ui_text.pick("Expand value sources", "Раскрыть источники значения")));
				menu.Items.Add(this.action_item(reference, research_action.sources, ui_text.pick("Start from this value", "Начать с этого значения"))); return;
			}
			if (reference is not MethodDef method) return;
			menu.Items.Add(this.action_item(method, research_action.expand, ui_text.pick("Expand here (keep root)", "Раскрыть здесь (сохранить корень)")));
			menu.Items.Add(this.action_item(method, research_action.collapse, ui_text.pick("Collapse branch", "Свернуть ветку")));
			var pin = this.action_item(method, research_action.pin, ui_text.pick("Pin in this view", "Закрепить во фрагменте")); pin.IsCheckable = true; pin.IsChecked = this.is_pinned(method); menu.Items.Add(pin);
			menu.Items.Add(new Separator());
			menu.Items.Add(this.action_item(method, research_action.evidence, ui_text.pick("Call sites and evidence…", "Места вызовов и основания…")));
			menu.Items.Add(this.action_item(method, research_action.sources, ui_text.pick("Address and argument sources…", "Источники адреса и аргументов…")));
			menu.Items.Add(new Separator());
			var annotations = new MenuItem { Header = ui_text.pick("Labels and groups", "Подписи и группы") };
			annotations.Items.Add(this.action_item(method, research_action.alias, ui_text.pick("Alias…", "Псевдоним…")));
			annotations.Items.Add(this.action_item(method, research_action.note, ui_text.pick("Note…", "Заметка…")));
			annotations.Items.Add(this.action_item(method, research_action.group, ui_text.pick("Group…", "Группа…")));
			var compact = this.action_item(method, research_action.compact, ui_text.pick("Compact card", "Компактная карточка")); compact.IsCheckable = true; compact.IsChecked = this.research_store?.get(method).compact == true; annotations.Items.Add(compact);
			if (this.research_store?.get(method).group is string name && name.Length != 0) annotations.Items.Add(this.action_item(method, research_action.collapse_group, ui_text.pick("Collapse group", "Свернуть группу")));
			menu.Items.Add(annotations);
			menu.Items.Add(new Separator());
		}
		graph_layout_result stabilize_layout(graph_presentation graph, graph_layout_result layout, IReadOnlyDictionary<object, highlighted_snippet> snippets, bool preserve) {
			if (!preserve) return layout;
			var rectangles = layout.rectangles.ToDictionary(pair => pair.Key, pair => pair.Value);
			bool changed = false;
			if (preserve) foreach (var node in graph.nodes) {
				var rect = rectangles[node.reference];
				if (this.node_rects.TryGetValue(node.reference, out var old) && Math.Abs(old.Width - rect.Width) < 0.5 && this.measured_snippets.TryGetValue(node.reference, out var measured) && snippets.TryGetValue(node.reference, out var snippet) && measured.text == snippet.text && measured.is_truncated == snippet.is_truncated) { changed |= Math.Abs(rect.Height - old.Height) >= 0.5; rect.Height = old.Height; rectangles[node.reference] = rect; }
			}
			return changed ? layout with { rectangles = layout.horizontal!.compact(rectangles, default) } : layout;
		}
		void load_manual_positions(graph_presentation graph) {
			if (this.research_store is null || graph.nodes.FirstOrDefault(node => node.is_root)?.reference is not MethodDef root) return;
			foreach (var node in graph.nodes.Where(node => node.reference is MethodDef)) if (this.research_store.positions(root).TryGetValue(research_store.key((MethodDef)node.reference), out var point) && point.manual && double.IsFinite(point.x) && double.IsFinite(point.y) && point.x >= 0 && point.y >= 0) this.manual_positions[call_graph_surface.reference_key(node.reference)] = new Point(point.x, point.y);
		}
		void compact_node_positions(HashSet<object> changed) {
			if (this.vertical_layout || this.dragged_node_reference is not null || this.node_rects.Count == 0) return;
			var next = this.horizontal_layout?.compact(this.node_rects, default) ?? graph_layout.compact_horizontal(new graph_presentation(this.node_presentations.Values.ToArray(), this.outgoing_ports.Values.SelectMany(links => links).ToArray()), this.node_rects, default);
			bool avoid_manual = this.manual_positions.Count != 0;
			var occupied = this.node_rects.Where(pair => this.manual_positions.ContainsKey(call_graph_surface.reference_key(pair.Key))).Select(pair => pair.Value).ToList();
			foreach (var node in this.node_presentations.Values.OrderBy(node => node.layer).ThenBy(node => next[node.reference].Top)) {
				if (this.manual_positions.ContainsKey(call_graph_surface.reference_key(node.reference))) continue;
				var rect = next[node.reference];
				while (avoid_manual) { var collisions = occupied.Where(other => { var padded = other; padded.Inflate(12, 15); return padded.IntersectsWith(rect); }).ToArray(); if (collisions.Length == 0) break; rect.Y = collisions.Max(other => other.Bottom) + call_graph_surface.node_gap; }
				if (avoid_manual) occupied.Add(rect);
				if (Math.Abs(rect.Top - this.node_rects[node.reference].Top) < 0.5) continue;
				this.node_rects[node.reference] = rect; changed.Add(node.reference);
				if (this.node_borders.TryGetValue(node.reference, out var border)) Canvas.SetTop(border, rect.Top);
			}
		}
		internal void save_positions() {
			if (this.research_store is null || this.root_reference is not MethodDef root || this.vertical_layout) return;
			var positions = this.research_store.positions(root).Where(pair => pair.Value.manual).ToDictionary(pair => pair.Key, pair => pair.Value);
			foreach (var pair in this.node_rects.Where(pair => pair.Key is MethodDef && this.manual_positions.ContainsKey(call_graph_surface.reference_key(pair.Key)))) positions[research_store.key((MethodDef)pair.Key)] = new research_position(pair.Value.X, pair.Value.Y, true);
			this.research_store.set_positions(root, positions);
		}
		void initialize_research_menu() {
			var menu = new ContextMenu(); var fit = new MenuItem { Header = ui_text.pick("Fit entire graph", "Вписать весь граф") }; fit.Click += (_, __) => this.fit_all(); menu.Items.Add(fit);
			var center = new MenuItem { Header = ui_text.center_root }; center.Click += (_, __) => this.center_root(); menu.Items.Add(center);
			ContextMenu = menu;
		}
		internal void fit_all() {
			if (this.node_rects.Count == 0 || this.scroll_viewer.ViewportWidth < 10 || this.scroll_viewer.ViewportHeight < 10) return;
			var bounds = Rect.Empty; foreach (var rect in this.node_rects.Values) bounds.Union(rect);
			this.zoom = Math.Clamp(Math.Min((this.scroll_viewer.ViewportWidth - 40) / Math.Max(1, bounds.Width), (this.scroll_viewer.ViewportHeight - 40) / Math.Max(1, bounds.Height)), 0.01, 1);
			this.scroll_viewer.UpdateLayout(); this.scroll_viewer.ScrollToHorizontalOffset(this.pan_host.Padding.Left + (bounds.Left + bounds.Width / 2) * this.zoom - this.scroll_viewer.ViewportWidth / 2); this.scroll_viewer.ScrollToVerticalOffset(this.pan_host.Padding.Top + (bounds.Top + bounds.Height / 2) * this.zoom - this.scroll_viewer.ViewportHeight / 2); this.update_visible_nodes();
		}
	}
}
