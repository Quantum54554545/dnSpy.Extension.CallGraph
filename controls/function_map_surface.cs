using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph.Controls {
	sealed class function_map_surface : FrameworkElement {
		sealed class graph_visual_value : FrameworkElement {
			readonly DrawingVisual drawing = new DrawingVisual();
			public graph_visual_value() { AddVisualChild(this.drawing); IsHitTestVisible = false; }
			public DrawingContext render_open() => this.drawing.RenderOpen();
			protected override int VisualChildrenCount => 1;
			protected override Visual GetVisualChild(int index) => index == 0 ? this.drawing : throw new ArgumentOutOfRangeException(nameof(index));
		}
		sealed class geometry_batch : IDisposable {
			readonly List<StreamGeometry> geometries = new List<StreamGeometry>();
			StreamGeometry? current;
			StreamGeometryContext? context;
			int count;
			public IReadOnlyList<StreamGeometry> geometries_value => this.geometries;
			public StreamGeometryContext next() {
				if (this.context is null || this.count >= 16) {
					Dispose();
					this.current = new StreamGeometry();
					this.geometries.Add(this.current);
					this.context = this.current.Open(); this.count = 0;
				}
				this.count++; return this.context;
			}
			public void Dispose() {
				this.context?.Close(); this.context = null;
				if (this.current is not null && !this.current.IsFrozen) this.current.Freeze();
			}
		}
		const double minimum_zoom = 0.08;
		const double maximum_zoom = 12;
		const double hit_cell_size = 48;
		readonly Dictionary<MethodDef, Point> positions = new Dictionary<MethodDef, Point>();
		readonly Dictionary<(int x, int y), List<MethodDef>> hit_cells = new Dictionary<(int, int), List<MethodDef>>();
		readonly Dictionary<MethodDef, List<call_graph_link>> incident_links = new Dictionary<MethodDef, List<call_graph_link>>();
		readonly DispatcherTimer geometry_timer;
		readonly graph_visual_value graph_visual = new graph_visual_value();
		readonly DrawingVisual overlay_visual = new DrawingVisual();
		readonly MatrixTransform graph_transform = new MatrixTransform();
		bool graph_drawing_dirty = true;
		double drawn_zoom;
		int graph_draw_count;
		readonly record struct text_key(string text, string font, double size, Brush brush, double width, double height, double dpi, string culture);
		readonly Dictionary<text_key, FormattedText> text_cache = new();
		readonly Dictionary<FormattedText, DrawingGroup> text_drawings = new();
		readonly Dictionary<string, Brush> fallback_brushes = new();
		readonly Dictionary<MethodDef, string> method_details = new();
		bool has_possible_targets;
		CancellationTokenSource? geometry_cancellation;
		int geometry_version;
		IThemeService? theme_service;
		call_graph_slice? slice;
		call_graph_index? index;
		MethodDef? root;
		MethodDef? hovered;
		MethodDef? selected;
		IReadOnlyList<StreamGeometry>? edge_geometry;
		IReadOnlyList<StreamGeometry>? incoming_edge_geometry;
		IReadOnlyList<StreamGeometry>? outgoing_edge_geometry;
		IReadOnlyList<StreamGeometry>? possible_edge_geometry;
		IReadOnlyList<StreamGeometry>? edge_arrow_geometry;
		IReadOnlyList<StreamGeometry>? incoming_arrow_geometry;
		IReadOnlyList<StreamGeometry>? outgoing_arrow_geometry;
		IReadOnlyList<StreamGeometry>? possible_arrow_geometry;
		IReadOnlyList<StreamGeometry>? caller_geometry;
		IReadOnlyList<StreamGeometry>? callee_geometry;
		IReadOnlyList<StreamGeometry>? neutral_geometry;
		double zoom = 1;
		Vector pan;
		Point drag_start;
		Vector pan_start;
		Point dragged_method_start;
		Point pointer_position;
		Point preview_position;
		MethodDef? dragged_method;
		MethodDef? preview_method;
		highlighted_snippet? preview_snippet;
		FormattedText? preview_code;
		Size preview_text_size;
		double preview_text_dpi;
		bool syntax_highlighting = true;
		internal void configure(call_graph_settings settings) { this.syntax_highlighting = settings.syntax_highlighting; this.clear_text_cache(); InvalidateVisual(); }
		bool drag_candidate;
		bool dragging;
		bool geometry_update_pending;
		MouseButton drag_button;
		Action? pending_render;
		CancellationTokenSource? layout_cancellation;
		int render_version;

		public event EventHandler<graph_reference_event_args>? root_requested;
		public event EventHandler<graph_reference_event_args>? navigate_requested;
		public event EventHandler<graph_reference_event_args>? selection_changed;
		public event EventHandler<graph_reference_event_args>? code_preview_requested;

		public function_map_surface() {
			AddVisualChild(this.graph_visual);
			AddVisualChild(this.overlay_visual);
			this.graph_visual.RenderTransform = this.graph_transform;
			Focusable = true;
			ClipToBounds = true;
			Cursor = Cursors.Arrow;
			SnapsToDevicePixels = true;
			this.geometry_timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) {
				Interval = TimeSpan.FromMilliseconds(40),
			};
			this.geometry_timer.Tick += this.geometry_timer_tick;
		}

		protected override int VisualChildrenCount => 2;
		protected override Visual GetVisualChild(int index) => index == 0 ? this.graph_visual : index == 1 ? this.overlay_visual : throw new ArgumentOutOfRangeException(nameof(index));
		protected override Size MeasureOverride(Size available_size) { this.graph_visual.Measure(new Size(1, 1)); return new Size(); }
		protected override Size ArrangeOverride(Size final_size) { this.graph_visual.Arrange(new Rect(0, 0, 1, 1)); return final_size; }

		internal void set_theme_service(IThemeService value) { this.theme_service = value; this.clear_text_cache(); this.graph_drawing_dirty = true; InvalidateVisual(); }
		internal bool is_showing(MethodDef method) => ReferenceEquals(this.root, method);
		internal void cancel_pending_render() { this.pending_render = null; this.layout_cancellation?.Cancel(); this.geometry_cancellation?.Cancel(); this.geometry_version++; this.render_version++; }

		public void clear() {
			this.cancel_pending_render();
			this.pending_render = null;
			this.drag_candidate = this.dragging = false;
			if (IsMouseCaptured) ReleaseMouseCapture();
			Cursor = Cursors.Arrow;
			this.slice = null;
			this.clear_text_cache(); this.method_details.Clear();
			this.index = null;
			this.root = this.hovered = this.selected = null;
			this.dragged_method = this.preview_method = null;
			this.preview_snippet = null;
			this.geometry_timer.Stop();
			this.geometry_update_pending = false;
			this.positions.Clear();
			this.hit_cells.Clear();
			this.incident_links.Clear();
			this.edge_geometry = this.incoming_edge_geometry = this.outgoing_edge_geometry = null;
			this.possible_edge_geometry = null;
			this.edge_arrow_geometry = this.incoming_arrow_geometry = this.outgoing_arrow_geometry = null;
			this.possible_arrow_geometry = null;
			this.caller_geometry = this.callee_geometry = this.neutral_geometry = null;
			using (this.graph_visual.render_open()) { }
			using (this.overlay_visual.RenderOpen()) { }
			InvalidateVisual();
		}

		public async void render(call_graph_slice value, call_graph_index? graph_index = null) {
			int version = ++this.render_version;
			this.geometry_cancellation?.Cancel(); this.geometry_version++;
			this.layout_cancellation?.Cancel();
			if (this.drag_candidate) {
				this.pending_render = () => this.render(value, graph_index);
				return;
			}
			var next_root = value.nodes.FirstOrDefault(node => node.is_root)?.method;
			this.layout_cancellation?.Dispose(); this.layout_cancellation = new CancellationTokenSource();
			var token = this.layout_cancellation.Token;
			var old_layers = this.slice?.nodes.ToDictionary(node => node.method, node => node.layer);
			var retained_positions = ReferenceEquals(this.root, next_root) && this.slice?.direction == value.direction
				? value.nodes.Where(node => old_layers?.TryGetValue(node.method, out int layer) == true && layer == node.layer && this.positions.ContainsKey(node.method)).ToDictionary(node => node.method, node => this.positions[node.method]) : null;
			(Dictionary<MethodDef, Point> positions, map_geometry geometry) prepared;
			try {
				prepared = await Task.Run(() => {
					var next = graph_layout.map(value, token, retained_positions);
					return (next: next, function_map_surface.create_geometry(value, next, next_root, null, token));
				}, token);
			}
			catch (OperationCanceledException) { return; }
			catch (Exception error) { if (version == this.render_version) ToolTip = ui_text.render_failure(error.Message); return; }
			if (version != this.render_version || token.IsCancellationRequested) return;
			if (this.drag_candidate) { this.pending_render = () => this.render(value, graph_index); return; }
			bool preserve = this.root is not null && ReferenceEquals(this.root, next_root) && this.slice?.direction == value.direction;
			var previous_selected = this.selected;
			this.drag_candidate = this.dragging = false;
			this.dragged_method = null;
			if (IsMouseCaptured) ReleaseMouseCapture();
			Cursor = Cursors.Arrow;
			this.slice = value;
			this.clear_text_cache(); this.method_details.Clear(); this.has_possible_targets = value.links.Any(link => link.is_potential);
			this.index = graph_index;
			this.root = next_root;
			this.selected = preserve && value.nodes.Any(node => ReferenceEquals(node.method, previous_selected)) ? previous_selected : this.root;
			this.hovered = null;
			this.hide_code_preview();
			this.incident_links.Clear();
			foreach (var link in value.links) {
				if (!this.incident_links.TryGetValue(link.source, out var outgoing)) this.incident_links[link.source] = outgoing = new List<call_graph_link>();
				outgoing.Add(link);
				if (ReferenceEquals(link.source, link.target)) continue;
				if (!this.incident_links.TryGetValue(link.target, out var incoming)) this.incident_links[link.target] = incoming = new List<call_graph_link>();
				incoming.Add(link);
			}
			this.positions.Clear(); foreach (var pair in prepared.positions) this.positions[pair.Key] = pair.Value;
			this.rebuild_hit_index(); this.apply_geometry(prepared.geometry);
			if (!preserve) this.reset_view(); else InvalidateVisual();
		}

		public void reset_view() {
			this.zoom = 1;
			this.pan = new Vector();
			InvalidateVisual();
		}

		public void center_root() => this.reset_view();
		internal MethodDef? resolve_shortcut_method(Point? pointer) => (pointer is Point point ? this.hit_test_method(point) : null) ?? this.selected;
		internal MethodDef? shortcut_method => this.resolve_shortcut_method(IsMouseOver ? Mouse.GetPosition(this) : null);

		public bool focus_method(MethodDef method) {
			if (!this.positions.TryGetValue(method, out var point))
				return false;
			this.selected = method;
			this.hovered = null;
			this.pan = new Vector(-point.X * this.zoom, -point.Y * this.zoom);
			this.selection_changed?.Invoke(this, new graph_reference_event_args(method));
			InvalidateVisual();
			return true;
		}

		sealed record map_geometry(IReadOnlyList<StreamGeometry> part0, IReadOnlyList<StreamGeometry> part1, IReadOnlyList<StreamGeometry> part2, IReadOnlyList<StreamGeometry> part3, IReadOnlyList<StreamGeometry> part4, IReadOnlyList<StreamGeometry> part5, IReadOnlyList<StreamGeometry> part6, IReadOnlyList<StreamGeometry> part7, IReadOnlyList<StreamGeometry> part8, IReadOnlyList<StreamGeometry> part9, IReadOnlyList<StreamGeometry> part10);
		static map_geometry create_geometry(call_graph_slice value, IReadOnlyDictionary<MethodDef, Point> points, MethodDef? map_root, MethodDef? excluded, CancellationToken token) {
			var edges = new geometry_batch();
			var incoming_edges = new geometry_batch();
			var outgoing_edges = new geometry_batch();
			var possible_edges = new geometry_batch();
			var edge_arrows = new geometry_batch();
			var incoming_arrows = new geometry_batch();
			var outgoing_arrows = new geometry_batch();
			var possible_arrows = new geometry_batch();
			using (edges) using (incoming_edges) using (outgoing_edges) using (possible_edges)
			using (edge_arrows) using (incoming_arrows) using (outgoing_arrows) using (possible_arrows) {
				foreach (var link in value.links) {
					token.ThrowIfCancellationRequested();
					if (excluded is not null && (ReferenceEquals(link.source, excluded) || ReferenceEquals(link.target, excluded))) continue;
					if (!points.TryGetValue(link.source, out var from) || !points.TryGetValue(link.target, out var to))
						continue;
					bool incoming = link.target == map_root;
					bool outgoing = link.source == map_root;
					var context = (link.is_potential ? possible_edges : incoming ? incoming_edges : outgoing ? outgoing_edges : edges).next();
					var arrow_context = (link.is_potential ? possible_arrows : incoming ? incoming_arrows : outgoing ? outgoing_arrows : edge_arrows).next();
					context.BeginFigure(from, false, false);
					context.LineTo(to, true, false);
					function_map_surface.append_arrow(arrow_context, from, to, link.target == map_root ? 11 : 7, incoming || outgoing ? 5.4 : 3.8);
				}
			}

			var callers = new geometry_batch();
			var callees = new geometry_batch();
			var neutral = new geometry_batch();
			using (callers) using (callees) using (neutral) {
				foreach (var node in value.nodes) {
					if (node.is_root || excluded is not null && ReferenceEquals(node.method, excluded) || !points.TryGetValue(node.method, out var point))
						continue;
					var context = (node.layer < 0 ? callers : node.layer > 0 ? callees : neutral).next();
					function_map_surface.append_diamond(context, point, 5.2);
				}
			}
			return new map_geometry(edges.geometries_value, incoming_edges.geometries_value, outgoing_edges.geometries_value, possible_edges.geometries_value, edge_arrows.geometries_value, incoming_arrows.geometries_value, outgoing_arrows.geometries_value, possible_arrows.geometries_value, callers.geometries_value, callees.geometries_value, neutral.geometries_value);
		}
		void apply_geometry(map_geometry geometry) {
			this.edge_geometry = geometry.part0;
			this.incoming_edge_geometry = geometry.part1;
			this.outgoing_edge_geometry = geometry.part2;
			this.possible_edge_geometry = geometry.part3;
			this.edge_arrow_geometry = geometry.part4;
			this.incoming_arrow_geometry = geometry.part5;
			this.outgoing_arrow_geometry = geometry.part6;
			this.possible_arrow_geometry = geometry.part7;
			this.caller_geometry = geometry.part8;
			this.callee_geometry = geometry.part9;
			this.neutral_geometry = geometry.part10;
			this.graph_drawing_dirty = true;
		}
		async void build_geometry() {
			this.geometry_cancellation?.Cancel(); this.geometry_cancellation?.Dispose();
			this.geometry_cancellation = new CancellationTokenSource();
			var token = this.geometry_cancellation.Token; int version = ++this.geometry_version;
			var current = this.slice; if (current is null) return;
			var points = new Dictionary<MethodDef, Point>(this.positions); var map_root = this.root; var excluded = this.dragging ? this.dragged_method : null;
			try {
				var geometry = await Task.Run(() => function_map_surface.create_geometry(current, points, map_root, excluded, token), token);
				if (version != this.geometry_version || token.IsCancellationRequested || !ReferenceEquals(current, this.slice)) return;
				this.apply_geometry(geometry); InvalidateVisual();
			}
			catch (OperationCanceledException) { }
			catch (Exception error) { if (version == this.geometry_version) ToolTip = ui_text.render_failure(error.Message); }
		}
		static void append_arrow(StreamGeometryContext context, Point from, Point to, double target_radius, double size) {
			double dx = to.X - from.X;
			double dy = to.Y - from.Y;
			double length = Math.Sqrt(dx * dx + dy * dy);
			if (length < target_radius + size * 2)
				return;
			double ux = dx / length;
			double uy = dy / length;
			var tip = new Point(to.X - ux * target_radius, to.Y - uy * target_radius);
			var base_point = new Point(tip.X - ux * size * 1.8, tip.Y - uy * size * 1.8);
			double px = -uy * size;
			double py = ux * size;
			context.BeginFigure(tip, true, true);
			context.LineTo(new Point(base_point.X + px, base_point.Y + py), true, false);
			context.LineTo(new Point(base_point.X - px, base_point.Y - py), true, false);
		}

		static void append_diamond(StreamGeometryContext context, Point point, double radius) {
			context.BeginFigure(new Point(point.X, point.Y - radius), true, true);
			context.LineTo(new Point(point.X + radius, point.Y), true, false);
			context.LineTo(new Point(point.X, point.Y + radius), true, false);
			context.LineTo(new Point(point.X - radius, point.Y), true, false);
		}

		protected override void OnRender(DrawingContext drawing_context) {
			base.OnRender(drawing_context);
			drawing_context.DrawRectangle(this.resource_brush("SystemColorsWindow", Color.FromRgb(30, 30, 30)), null,
				new Rect(0, 0, ActualWidth, ActualHeight));
			if (this.slice is null)
				return;

			this.graph_transform.Matrix = new Matrix(this.zoom, 0, 0, this.zoom, ActualWidth / 2 + this.pan.X, ActualHeight / 2 + this.pan.Y);
			if (this.graph_drawing_dirty || this.drawn_zoom <= 0 || this.zoom / this.drawn_zoom > 1.3 || this.zoom / this.drawn_zoom < 1 / 1.3) {
				using (var graph_context = this.graph_visual.render_open()) this.draw_graph(graph_context);
				this.graph_drawing_dirty = false;
				this.drawn_zoom = this.zoom;
			}
			using (var overlay_context = this.overlay_visual.RenderOpen()) this.draw_overlay(overlay_context);
		}

		void draw_graph(DrawingContext drawing_context) {
			this.graph_draw_count++;
			var text_brush = this.resource_brush("SystemColorsWindowText", Colors.Gainsboro);
			var caller_brush = this.resource_brush("SearchMatchHighlight", Color.FromRgb(95, 145, 225));
			var callee_brush = this.resource_brush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var accent = this.resource_brush("HyperlinkNormal", Color.FromRgb(86, 190, 235));
			if (this.slice is not null) {
				var address_pen = new Pen(Brushes.MediumPurple, 1.2 / Math.Sqrt(this.zoom)) { DashStyle = new DashStyle(new[] { 1.0, 4.0 }, 0) };
				foreach (var reference in this.slice.diagnostics.Where(note => note.kind == graph_diagnostic_kind.address_reference && note.target is not null).DistinctBy(note => (method: note.method, target: note.target)))
					if (this.positions.TryGetValue(reference.method, out var from) && this.positions.TryGetValue(reference.target!, out var to) && !(this.dragging && (reference.method == this.dragged_method || reference.target == this.dragged_method))) drawing_context.DrawLine(address_pen, from, to);
			}
			if (this.possible_edge_geometry is not null) {
				var possible_pen = new Pen(accent, 1.05 / Math.Sqrt(this.zoom)) {
					DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
				};
				drawing_context.PushOpacity(0.52);
				function_map_surface.draw_batch(drawing_context, null, possible_pen, this.possible_edge_geometry);
				function_map_surface.draw_batch(drawing_context, accent, null, this.possible_arrow_geometry);
				drawing_context.Pop();
			}
			if (this.edge_geometry is not null) {
				drawing_context.PushOpacity(0.22);
				function_map_surface.draw_batch(drawing_context, null, new Pen(text_brush, 0.75 / Math.Sqrt(this.zoom)), this.edge_geometry);
				function_map_surface.draw_batch(drawing_context, text_brush, null, this.edge_arrow_geometry);
				drawing_context.Pop();
			}
			if (this.incoming_edge_geometry is not null) {
				drawing_context.PushOpacity(0.72);
				function_map_surface.draw_batch(drawing_context, null, new Pen(caller_brush, 1.45 / Math.Sqrt(this.zoom)), this.incoming_edge_geometry);
				function_map_surface.draw_batch(drawing_context, caller_brush, null, this.incoming_arrow_geometry);
				drawing_context.Pop();
			}
			if (this.outgoing_edge_geometry is not null) {
				drawing_context.PushOpacity(0.78);
				function_map_surface.draw_batch(drawing_context, null, new Pen(callee_brush, 1.45 / Math.Sqrt(this.zoom)), this.outgoing_edge_geometry);
				function_map_surface.draw_batch(drawing_context, callee_brush, null, this.outgoing_arrow_geometry);
				drawing_context.Pop();
			}
			if (this.neutral_geometry is not null)
				function_map_surface.draw_batch(drawing_context, this.resource_brush("SystemColorsGrayText", Color.FromRgb(140, 145, 155)), null, this.neutral_geometry);
			if (this.caller_geometry is not null)
				function_map_surface.draw_batch(drawing_context, caller_brush, null, this.caller_geometry);
			if (this.callee_geometry is not null)
				function_map_surface.draw_batch(drawing_context, callee_brush, null, this.callee_geometry);
			if (this.root is not null && !(this.dragging && ReferenceEquals(this.root, this.dragged_method)) && this.positions.TryGetValue(this.root, out var root_point))
				drawing_context.DrawEllipse(this.resource_brush("ToolBarButtonChecked", Color.FromRgb(245, 190, 75)),
					new Pen(accent, 1.8 / this.zoom), root_point, 9, 9);
		}

		void draw_overlay(DrawingContext drawing_context) {
			var caller_brush = this.resource_brush("SearchMatchHighlight", Color.FromRgb(95, 145, 225));
			var callee_brush = this.resource_brush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var accent = this.resource_brush("HyperlinkNormal", Color.FromRgb(86, 190, 235));
			drawing_context.PushTransform(this.graph_transform);
			this.draw_dragged_method(drawing_context);
			this.draw_marker(drawing_context, this.selected, this.resource_brush("ToolBarButtonHoverBorder", Colors.White), 8);
			if (this.hovered != this.selected)
				this.draw_marker(drawing_context, this.hovered, accent, 7);
			drawing_context.Pop();

			this.draw_method_label(drawing_context, this.root, new Vector(12, -25), true);
			if (this.hovered != this.root)
				this.draw_method_label(drawing_context, this.hovered, new Vector(10, 10), false);
			else if (this.selected != this.root)
				this.draw_method_label(drawing_context, this.selected, new Vector(10, 10), false);
			this.draw_legend(drawing_context, caller_brush, callee_brush);
			this.draw_method_info(drawing_context, this.hovered ?? this.selected ?? this.root);
			this.draw_counter(drawing_context);
			this.draw_hint(drawing_context);
			this.draw_code_preview(drawing_context);
		}

		static void draw_batch(DrawingContext context, Brush? brush, Pen? pen, IReadOnlyList<StreamGeometry>? geometries) {
			if (geometries is null) return;
			foreach (var geometry in geometries) context.DrawGeometry(brush, pen, geometry);
		}

		void draw_dragged_method(DrawingContext context) {
			if (!this.dragging || this.dragged_method is null || !this.positions.TryGetValue(this.dragged_method, out var point)) return;
			var brush = this.resource_brush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var pen = new Pen(brush, 1.2 / Math.Sqrt(this.zoom));
			if (this.incident_links.TryGetValue(this.dragged_method, out var links)) {
				foreach (var link in links) {
					if (!this.positions.TryGetValue(link.source, out var from) || !this.positions.TryGetValue(link.target, out var to)) continue;
					context.DrawLine(pen, from, to);
					var arrow = new StreamGeometry();
					using (var arrow_context = arrow.Open()) function_map_surface.append_arrow(arrow_context, from, to, link.target == this.root ? 11 : 7, 3.8);
					arrow.Freeze(); context.DrawGeometry(brush, null, arrow);
				}
			}
			context.DrawEllipse(brush, null, point, this.dragged_method == this.root ? 9 : 5.2, this.dragged_method == this.root ? 9 : 5.2);
		}

		void draw_marker(DrawingContext drawing_context, MethodDef? method, Brush brush, double radius) {
			if (method is null || method == this.root || !this.positions.TryGetValue(method, out var point))
				return;
			drawing_context.DrawEllipse(null, new Pen(brush, 1.8 / this.zoom), point, radius, radius);
		}

		void draw_method_label(DrawingContext drawing_context, MethodDef? method, Vector offset, bool root_label) {
			if (method is null || !this.positions.TryGetValue(method, out var graph_point))
				return;
			Point point = this.graph_to_screen(graph_point) + offset;
			string label = root_label ? call_graph_index.signature(method) : call_graph_index.display_name(method);
			var text = this.cached_text(label, "Segoe UI", root_label ? 12 : 11, this.resource_brush("SystemColorsWindowText", Colors.Gainsboro), Math.Max(80, Math.Min(520, ActualWidth - Math.Max(0, point.X) - 12)));
			this.draw_cached_text(drawing_context, text, point);
		}

		void draw_legend(DrawingContext drawing_context, Brush caller_brush, Brush callee_brush) {
			if (ActualWidth < 280 || ActualHeight < 90)
				return;
			var callers = this.cached_text(ui_text.map_legend_callers, "Segoe UI Semibold", 10.5, caller_brush);
			var root_text = this.cached_text(ui_text.map_legend_root, "Segoe UI Semibold", 10.5, this.resource_brush("SystemColorsWindowText", Colors.Gainsboro));
			var callees = this.cached_text(ui_text.map_legend_callees, "Segoe UI Semibold", 10.5, callee_brush);
			var possible = this.cached_text(ui_text.map_legend_possible, "Segoe UI Semibold", 9.5, this.resource_brush("SystemColorsGrayText", Colors.Silver));
			double x = 18;
			double first_arrow_start = x + callers.Width + 10;
			double first_arrow_end = first_arrow_start + 25;
			double root_x = first_arrow_end + 7;
			double second_arrow_start = root_x + root_text.Width + 10;
			double second_arrow_end = second_arrow_start + 25;
			double callees_x = second_arrow_end + 7;
			double panel_width = Math.Max(callees_x + callees.Width + 10, this.has_possible_targets ? possible.Width + 59 : 0);
			var panel = new Rect(8, 8, panel_width, this.has_possible_targets ? 51 : 31);
			this.draw_overlay_panel(drawing_context, panel);
			this.draw_cached_text(drawing_context, callers, new Point(x, 16));
			this.draw_cached_text(drawing_context, root_text, new Point(root_x, 16));
			this.draw_cached_text(drawing_context, callees, new Point(callees_x, 16));
			function_map_surface.draw_legend_arrow(drawing_context, new Point(first_arrow_start, 23), new Point(first_arrow_end, 23), caller_brush);
			function_map_surface.draw_legend_arrow(drawing_context, new Point(second_arrow_start, 23), new Point(second_arrow_end, 23), callee_brush);
			if (this.has_possible_targets) {
				var possible_pen = new Pen(this.resource_brush("HyperlinkNormal", Color.FromRgb(86, 190, 235)), 1.2) {
					DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
				};
				drawing_context.DrawLine(possible_pen, new Point(x, 42), new Point(x + 28, 42));
				this.draw_cached_text(drawing_context, possible, new Point(x + 36, 34));
			}
		}

		static void draw_legend_arrow(DrawingContext drawing_context, Point from, Point to, Brush brush) {
			drawing_context.DrawLine(new Pen(brush, 1.4), from, to);
			var arrow = new StreamGeometry();
			using (var context = arrow.Open()) {
				context.BeginFigure(to, true, true);
				context.LineTo(new Point(to.X - 5, to.Y - 3), true, false);
				context.LineTo(new Point(to.X - 5, to.Y + 3), true, false);
			}
			drawing_context.DrawGeometry(brush, null, arrow);
		}

		void draw_method_info(DrawingContext drawing_context, MethodDef? method) {
			if (method is null || ActualWidth < 530 || ActualHeight < 180)
				return;
			double width = Math.Min(390, ActualWidth * 0.42);
			var panel = new Rect(ActualWidth - width - 9, 8, width, 130);
			this.draw_overlay_panel(drawing_context, panel);
			var heading = this.cached_text(call_graph_index.display_name(method), "Segoe UI Semibold", 12, this.resource_brush("SystemColorsWindowText", Colors.Gainsboro), width - 20);
			this.draw_cached_text(drawing_context, heading, new Point(panel.Left + 10, panel.Top + 8));
			if (!this.method_details.TryGetValue(method, out string? detail)) {
				int callers = this.index?.incoming_value(method).Count ?? 0;
				int callees = this.index?.outgoing_value(method).Count ?? 0;
				int call_sites = this.index?.outgoing_value(method).Sum(link => link.call_site_count) ?? 0;
				int instructions = method.HasBody ? method.Body.Instructions.Count : 0;
				detail = (method.DeclaringType?.FullName ?? "<Module>") + Environment.NewLine +
					$"token 0x{method.MDToken.Raw:X8}   •   " + ui_text.method_info(callers, callees, call_sites, instructions, method.IsStatic) + "\n" + analysis_details.summary(this.slice, method);
				this.method_details[method] = detail;
			}
			var details = this.cached_text(detail, "Segoe UI", 10.5, this.resource_brush("SystemColorsGrayText", Colors.Silver), width - 20, 94);
			this.draw_cached_text(drawing_context, details, new Point(panel.Left + 10, panel.Top + 31));
		}

		void draw_hint(DrawingContext drawing_context) {
			if (ActualWidth < 570)
				return;
			string value = ui_text.map_hint;
			var text = this.cached_text(value, "Segoe UI", 10.5, this.resource_brush("SystemColorsGrayText", Colors.Gray));
			this.draw_cached_text(drawing_context, text, new Point(Math.Max(9, ActualWidth - text.Width - 10),
				Math.Max(4, ActualHeight - text.Height - 7)));
		}

		void draw_code_preview(DrawingContext drawing_context) {
			if (this.preview_method is null || this.hovered != this.preview_method || this.dragging || ActualWidth < 260 || ActualHeight < 160)
				return;
			double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			double maximum_width = Math.Min(580, Math.Max(240, ActualWidth - 30));
			double maximum_height = Math.Min(360, Math.Max(100, ActualHeight - 30));
			var heading = this.cached_text(call_graph_index.signature(this.preview_method), "Segoe UI Semibold", 11.5, this.resource_brush("SystemColorsWindowText", Colors.Gainsboro), maximum_width - 20);
			var text_size = new Size(maximum_width, maximum_height);
			if (this.preview_code is null || this.preview_text_size != text_size || this.preview_text_dpi != dpi) {
				this.reset_preview_text(); this.preview_text_size = text_size; this.preview_text_dpi = dpi;
				this.preview_code = new FormattedText(this.preview_snippet?.text ?? ui_text.loading_preview, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Consolas"), 10.5, this.resource_brush("SystemColorsWindowText", Colors.Gainsboro), dpi) {
				MaxTextWidth = maximum_width - 20,
				MaxTextHeight = maximum_height - 43,
				Trimming = TextTrimming.CharacterEllipsis,
			};
				this.apply_preview_colors(this.preview_code, this.preview_snippet);
			}
			var code = this.preview_code;
			double width = Math.Min(maximum_width, Math.Max(280, Math.Max(heading.Width, code.Width) + 20));
			double height = Math.Min(maximum_height, 33 + code.Height + 10);
			double x = this.preview_position.X + 16;
			double y = this.preview_position.Y + 16;
			if (x + width > ActualWidth - 8)
				x = this.preview_position.X - width - 16;
			if (y + height > ActualHeight - 8)
				y = this.preview_position.Y - height - 16;
			x = Math.Max(8, Math.Min(x, ActualWidth - width - 8));
			y = Math.Max(8, Math.Min(y, ActualHeight - height - 8));
			var panel = new Rect(x, y, width, height);
			this.draw_overlay_panel(drawing_context, panel);
			this.draw_cached_text(drawing_context, heading, new Point(x + 10, y + 7));
			drawing_context.DrawLine(new Pen(this.resource_brush("CommonControlsTextBoxBorder", Color.FromRgb(75, 75, 80)), 1),
				new Point(x + 1, y + 29), new Point(x + width - 1, y + 29));
			this.draw_cached_text(drawing_context, code, new Point(x + 10, y + 34));
		}

		void apply_preview_colors(FormattedText text, highlighted_snippet? snippet) {
			if (snippet is null || this.theme_service is null || !this.syntax_highlighting)
				return;
			foreach (var span in snippet.spans) {
				if (span.start < 0 || span.start >= snippet.text.Length || span.length <= 0)
					continue;
				int length = Math.Min(span.length, snippet.text.Length - span.start);
				uint color_value = (uint)ColorType.Text + (uint)span.color;
				var color = this.theme_service.Theme.GetTextColor((ColorType)color_value);
				if (color.Foreground is not null)
					text.SetForegroundBrush(color.Foreground, span.start, length);
				if (color.FontWeight is not null)
					text.SetFontWeight(color.FontWeight.Value, span.start, length);
				if (color.FontStyle is not null)
					text.SetFontStyle(color.FontStyle.Value, span.start, length);
			}
		}

		void show_code_preview() {
			if (this.hovered is null || this.drag_candidate || this.dragging)
				return;
			this.preview_method = this.hovered;
			this.reset_preview_text();
			this.preview_snippet = null;
			this.preview_position = this.pointer_position;
			InvalidateVisual();
			this.code_preview_requested?.Invoke(this, new graph_reference_event_args(this.hovered));
		}

		void hide_code_preview() {
			this.reset_preview_text();
			this.preview_method = null;
			this.preview_snippet = null;
		}

		public void set_code_preview(MethodDef method, highlighted_snippet snippet) {
			if (!ReferenceEquals(method, this.preview_method) || !ReferenceEquals(method, this.hovered))
				return;
			this.preview_snippet = snippet;
			this.reset_preview_text();
			InvalidateVisual();
		}

		void draw_overlay_panel(DrawingContext drawing_context, Rect rect) {
			drawing_context.PushOpacity(0.92);
			drawing_context.DrawRoundedRectangle(this.resource_brush("EnvironmentBackground", Color.FromRgb(40, 40, 43)),
				new Pen(this.resource_brush("CommonControlsTextBoxBorder", Color.FromRgb(75, 75, 80)), 1), rect, 2, 2);
			drawing_context.Pop();
		}

		void draw_counter(DrawingContext drawing_context) {
			if (this.slice is null)
				return;
			string text_value = ui_text.methods_links(this.slice.nodes.Count, this.slice.links.Count, this.slice.is_truncated);
			var text = this.cached_text(text_value, "Segoe UI", 11, this.resource_brush("SystemColorsGrayText", Colors.Gray));
			this.draw_cached_text(drawing_context, text, new Point(9, Math.Max(4, ActualHeight - text.Height - 7)));
		}

		FormattedText cached_text(string value, string font, double size, Brush brush, double width = 0, double height = 0) {
			var key = new text_key(value, font, size, brush, width, height, VisualTreeHelper.GetDpi(this).PixelsPerDip, CultureInfo.CurrentUICulture.Name);
			if (this.text_cache.TryGetValue(key, out var text)) return text;
			if (this.text_cache.Count >= 128) this.clear_text_cache();
			text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font), size, brush, key.dpi);
			if (width > 0) { text.MaxTextWidth = width; text.Trimming = TextTrimming.CharacterEllipsis; }
			if (height > 0) text.MaxTextHeight = height;
			this.text_cache.Add(key, text); return text;
		}
		void clear_text_cache() { this.text_cache.Clear(); this.text_drawings.Clear(); this.reset_preview_text(); }
		void reset_preview_text() { if (this.preview_code is not null) this.text_drawings.Remove(this.preview_code); this.preview_code = null; }
		void draw_cached_text(DrawingContext context, FormattedText text, Point point) {
			if (!this.text_drawings.TryGetValue(text, out var drawing)) {
				drawing = new DrawingGroup(); using (var target = drawing.Open()) target.DrawText(text, new Point());
				this.text_drawings.Add(text, drawing);
			}
			context.PushTransform(new TranslateTransform(point.X, point.Y)); context.DrawDrawing(drawing); context.Pop();
		}
		Brush resource_brush(string key, Color fallback) {
			if (TryFindResource(key) is Brush brush) return brush;
			if (this.fallback_brushes.TryGetValue(key, out var cached)) return cached;
			var created = new SolidColorBrush(fallback); created.Freeze(); this.fallback_brushes.Add(key, created); return created;
		}

		Point graph_to_screen(Point point) => new Point(ActualWidth / 2 + this.pan.X + point.X * this.zoom,
			ActualHeight / 2 + this.pan.Y + point.Y * this.zoom);

		Point screen_to_graph(Point point) => new Point((point.X - ActualWidth / 2 - this.pan.X) / this.zoom,
			(point.Y - ActualHeight / 2 - this.pan.Y) / this.zoom);

		static (int x, int y) hit_cell(Point point) =>
			((int)Math.Floor(point.X / function_map_surface.hit_cell_size), (int)Math.Floor(point.Y / function_map_surface.hit_cell_size));

		void rebuild_hit_index() {
			this.hit_cells.Clear();
			foreach (var pair in this.positions)
				this.add_to_hit_index(pair.Key, pair.Value);
		}

		void add_to_hit_index(MethodDef method, Point point) {
			var cell = function_map_surface.hit_cell(point);
			if (!this.hit_cells.TryGetValue(cell, out var methods))
				this.hit_cells.Add(cell, methods = new List<MethodDef>());
			methods.Add(method);
		}

		void move_in_hit_index(MethodDef method, Point old_point, Point new_point) {
			var old_cell = function_map_surface.hit_cell(old_point);
			var new_cell = function_map_surface.hit_cell(new_point);
			if (old_cell == new_cell)
				return;
			if (this.hit_cells.TryGetValue(old_cell, out var old_methods)) {
				old_methods.Remove(method);
				if (old_methods.Count == 0)
					this.hit_cells.Remove(old_cell);
			}
			this.add_to_hit_index(method, new_point);
		}

		MethodDef? hit_test_method(Point screen_point) {
			Point graph_point = this.screen_to_graph(screen_point);
			double maximum_distance = Math.Max(8, 12 / this.zoom);
			double maximum_distance_squared = maximum_distance * maximum_distance;
			MethodDef? result = null;
			double best = maximum_distance_squared;
			var center_cell = function_map_surface.hit_cell(graph_point);
			int cell_radius = Math.Max(1, (int)Math.Ceiling(maximum_distance / function_map_surface.hit_cell_size));
			for (int x = center_cell.x - cell_radius; x <= center_cell.x + cell_radius; x++) {
				for (int y = center_cell.y - cell_radius; y <= center_cell.y + cell_radius; y++) {
					if (!this.hit_cells.TryGetValue((x: x, y: y), out var methods))
						continue;
					foreach (var method in methods) {
						Point position = this.positions[method];
						double dx = position.X - graph_point.X;
						double dy = position.Y - graph_point.Y;
						double distance = dx * dx + dy * dy;
						if (distance >= best)
							continue;
						best = distance;
						result = method;
					}
				}
			}
			return result;
		}

		void schedule_geometry_update() {
			this.geometry_update_pending = true;
			if (!this.geometry_timer.IsEnabled)
				this.geometry_timer.Start();
		}

		void geometry_timer_tick(object? sender, EventArgs e) {
			if (!this.geometry_update_pending) {
				this.geometry_timer.Stop();
				return;
			}
			this.geometry_update_pending = false;
			if (this.slice is null)
				return;
			InvalidateVisual();
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e) {
			if (e.Delta == 0)
				return;
			Point pointer = e.GetPosition(this);
			Point anchor = this.screen_to_graph(pointer);
			double next = Math.Max(function_map_surface.minimum_zoom, Math.Min(function_map_surface.maximum_zoom, this.zoom * Math.Pow(1.18, e.Delta / 120.0)));
			this.zoom = next;
			this.pan = new Vector(pointer.X - ActualWidth / 2 - anchor.X * this.zoom,
				pointer.Y - ActualHeight / 2 - anchor.Y * this.zoom);
			InvalidateVisual();
			e.Handled = true;
		}

		protected override void OnMouseDown(MouseButtonEventArgs e) {
			Focus();
			Point point = e.GetPosition(this);
			this.pointer_position = point;
			var method = this.hit_test_method(point);
			if (e.ChangedButton == MouseButton.Right) {
				if (method is not null)
					this.open_context_menu(method);
				e.Handled = true;
				return;
			}
			if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle)
				return;
			if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && method is not null) {
				this.root_requested?.Invoke(this, new graph_reference_event_args(method));
				e.Handled = true;
				return;
			}
			this.drag_button = e.ChangedButton;
			this.drag_candidate = true;
			this.dragging = false;
			this.drag_start = point;
			this.pan_start = this.pan;
			this.dragged_method = e.ChangedButton == MouseButton.Left ? method : null;
			if (this.dragged_method is not null)
				this.dragged_method_start = this.positions[this.dragged_method];
			this.hide_code_preview();
			CaptureMouse();
			e.Handled = true;
		}

		protected override void OnMouseMove(MouseEventArgs e) {
			Point point = e.GetPosition(this);
			this.pointer_position = point;
			if (this.drag_candidate) {
				bool pressed = this.drag_button == MouseButton.Left ? e.LeftButton == MouseButtonState.Pressed : e.MiddleButton == MouseButtonState.Pressed;
				if (!pressed) {
					this.end_drag(point);
					return;
				}
				Vector delta = point - this.drag_start;
				if (!this.dragging && (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
					Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)) {
					this.dragging = true;
					Cursor = this.dragged_method is null ? Cursors.ScrollAll : Cursors.SizeAll;
					if (this.dragged_method is not null) this.build_geometry();
				}
				if (this.dragging) {
					if (this.dragged_method is not null) {
						Point old_position = this.positions[this.dragged_method];
						Point new_position = this.dragged_method_start + delta / this.zoom;
						this.positions[this.dragged_method] = new_position;
						this.move_in_hit_index(this.dragged_method, old_position, new_position);
						this.schedule_geometry_update();
					}
					else {
						this.pan = this.pan_start + delta;
						InvalidateVisual();
					}
				}
				return;
			}

			var next = this.hit_test_method(point);
			if (next == this.hovered)
				return;
			this.hovered = next;
			ToolTip = null;
			Cursor = this.hovered is null ? Cursors.Arrow : Cursors.Hand;
			if (this.hovered is null) {
				this.hide_code_preview();
				InvalidateVisual();
			}
			else
				this.show_code_preview();
		}

		protected override void OnMouseUp(MouseButtonEventArgs e) {
			if (this.drag_candidate && e.ChangedButton == this.drag_button) {
				this.end_drag(e.GetPosition(this));
				e.Handled = true;
			}
		}

		protected override void OnLostMouseCapture(MouseEventArgs e) {
			base.OnLostMouseCapture(e);
			bool had_drag = this.drag_candidate || this.dragging;
			bool rebuild = this.dragging && this.dragged_method is not null;
			this.drag_candidate = this.dragging = false;
			this.dragged_method = null;
			this.geometry_timer.Stop(); this.geometry_update_pending = false;
			if (rebuild && this.slice is not null) { this.build_geometry(); InvalidateVisual(); }
			Cursor = Cursors.Arrow;
			if (had_drag) this.apply_pending_render();
		}

		protected override void OnMouseLeave(MouseEventArgs e) {
			base.OnMouseLeave(e);
			if (this.drag_candidate)
				return;
			this.hovered = null;
			this.hide_code_preview();
			Cursor = Cursors.Arrow;
			InvalidateVisual();
		}

		void end_drag(Point point) {
			bool was_dragging = this.dragging;
			var moved_method = this.dragged_method;
			this.drag_candidate = this.dragging = false;
			this.dragged_method = null;
			this.geometry_timer.Stop();
			if ((this.geometry_update_pending || was_dragging && moved_method is not null) && this.slice is not null) {
				this.geometry_update_pending = false;
				this.build_geometry();
			}
			ReleaseMouseCapture();
			Cursor = Cursors.Arrow;
			if (this.drag_button == MouseButton.Left) {
				var method = was_dragging ? moved_method : this.hit_test_method(point);
				if (method is not null) {
					this.selected = method;
					this.selection_changed?.Invoke(this, new graph_reference_event_args(method));
				}
			}
			this.hovered = this.hit_test_method(point);
			Cursor = this.hovered is null ? Cursors.Arrow : Cursors.Hand;
			if (this.hovered is null) {
				this.hide_code_preview();
				InvalidateVisual();
			}
			else
				this.show_code_preview();
			this.apply_pending_render();
		}

		void apply_pending_render() {
			var pending = this.pending_render;
			this.pending_render = null;
			pending?.Invoke();
		}

		void open_context_menu(MethodDef method) {
			var menu = new ContextMenu();
			foreach (var action in new[] { (research_action.expand, ui_text.pick("Expand here (keep root)", "Раскрыть здесь (сохранить корень)")), (research_action.collapse, ui_text.pick("Collapse branch", "Свернуть ветку")), (research_action.pin, ui_text.pick("Pin / unpin", "Закрепить / открепить")), (research_action.evidence, ui_text.pick("Call sites and evidence…", "Места вызовов и основания…")), (research_action.sources, ui_text.pick("Value sources…", "Источники значений…")) }) {
				var item = new MenuItem { Header = action.Item2 }; item.Click += (_, __) => this.research_requested?.Invoke(this, new research_action_event_args(method, action.Item1)); menu.Items.Add(item);
			}
			menu.Items.Add(new Separator());
			var explore = new MenuItem { Header = ui_text.explore_here, InputGestureText = call_graph_command_loader.shortcut_display_text, FontWeight = FontWeights.SemiBold };
			explore.Click += (_, __) => this.root_requested?.Invoke(this, new graph_reference_event_args(method));
			menu.Items.Add(explore);
			var open = new MenuItem { Header = ui_text.open_source };
			open.Click += (_, __) => this.navigate_requested?.Invoke(this, new graph_reference_event_args(method));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = ui_text.copy_full_name };
			copy.Click += (_, __) => Clipboard.SetText(call_graph_index.display_name(method));
			menu.Items.Add(copy);
			menu.PlacementTarget = this;
			menu.IsOpen = true;
		}
		internal event EventHandler<research_action_event_args>? research_requested;
	}
}
