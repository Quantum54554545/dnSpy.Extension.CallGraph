using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph.Controls {
	sealed partial class call_graph_surface : UserControl {
		sealed class edge_layer_value : FrameworkElement {
			readonly call_graph_surface owner;
			DrawingGroup? drawing;
			int drawn_edges;
			public edge_layer_value(call_graph_surface owner) {
				this.owner = owner; ToolTip = " ";
				MouseLeftButtonDown += (_, event_args) => {
					var point = event_args.GetPosition(this); var pen = new Pen(Brushes.Black, 9 / owner.zoom);
					var hit = owner.edge_visuals.LastOrDefault(edge => edge_layer_value.near_curve(edge.curve, point, pen.Thickness) && edge.curve.StrokeContains(pen, point));
					if (hit is not null) { owner.end_pan(); owner.edge_requested?.Invoke(owner, new graph_reference_event_args(hit.link)); event_args.Handled = true; }
				};
				ToolTipOpening += (_, e) => {
					Point point = Mouse.GetPosition(this);
					var pen = new Pen(Brushes.Black, 7 / owner.zoom);
					var hit = owner.edge_visuals.FirstOrDefault(edge => edge_layer_value.near_curve(edge.curve, point, pen.Thickness) && edge.curve.StrokeContains(pen, point));
					if (hit is null) e.Handled = true; else ToolTip = hit.link.tool_tip;
				};
			}
			public void invalidate_drawing(bool append = false) { if (!append) { this.drawing = null; this.drawn_edges = 0; } InvalidateVisual(); }
			static bool near_curve(Geometry geometry, Point point, double radius) { var bounds = geometry.Bounds; bounds.Inflate(radius, radius); return bounds.Contains(point); }
			protected override void OnRender(DrawingContext context) {
				if (this.drawing is null) this.drawing = new DrawingGroup();
				if (this.drawn_edges < this.owner.edge_visuals.Count) { using (var target = this.drawing.Append()) this.owner.draw_edges(target, this.drawn_edges); this.drawn_edges = this.owner.edge_visuals.Count; }
				context.DrawDrawing(this.drawing);
			}
		}
		sealed class edge_visual {
			public graph_link_presentation link { get; }
			public Geometry curve { get; set; }
			public Geometry arrow { get; set; }
			public string? label { get; }
			public double label_size { get; }
			public Point label_point { get; set; }
			public Vector label_offset { get; }

			public edge_visual(graph_link_presentation link, Geometry curve, Geometry arrow,
				string? label, double label_size, Vector label_offset, Point label_point) {
				this.link = link;
				this.curve = curve;
				this.arrow = arrow;
				this.label = label;
				this.label_size = label_size; this.label_point = label_point;
				this.label_offset = label_offset;
			}
		}

		readonly struct edge_placement {
			public Point from { get; }
			public Point to { get; }
			public Point control1 { get; }
			public Point control2 { get; }
			public Point label_point { get; }

			public edge_placement(Point from, Point to, Point control1, Point control2, Point label_point) {
				this.from = from;
				this.to = to;
				this.control1 = control1;
				this.control2 = control2;
				this.label_point = label_point;
			}
		}

		const double column_gap = 115;
		const double node_gap = 30;
		const double canvas_margin = 60;
		const double minimum_horizontal_pan_space = 420;
		const double minimum_vertical_pan_space = 260;
		const double default_node_width = graph_layout.maximum_automatic_width;
		const double header_height = 47;
		const double minimum_node_height = 78;

		readonly ScrollViewer scroll_viewer;
		readonly Border pan_host;
		readonly Canvas canvas;
		readonly Canvas extent_host;
		readonly ScaleTransform scale_transform;
		readonly edge_layer_value edge_layer;
		readonly DispatcherTimer visibility_timer;
		bool visibility_update_pending;
		IThemeService? theme_service;
		readonly Dictionary<object, Border> node_borders = new Dictionary<object, Border>();
		readonly Dictionary<object, Border> cached_borders = new Dictionary<object, Border>();
		readonly Dictionary<object, Rect> node_rects = new Dictionary<object, Rect>();
		readonly List<edge_visual> edge_visuals = new List<edge_visual>();
		readonly Dictionary<(string text, double size, double dpi), DrawingGroup> edge_label_drawings = new();
		readonly Dictionary<object, List<edge_visual>> incident_edges = new Dictionary<object, List<edge_visual>>();
		readonly Dictionary<object, highlighted_snippet> measured_snippets = new Dictionary<object, highlighted_snippet>();
		CancellationTokenSource? layout_cancellation;
		bool layout_pending;
		internal bool is_rendering => this.layout_pending;
		readonly Dictionary<object, graph_node_presentation> node_presentations = new Dictionary<object, graph_node_presentation>();
		readonly Dictionary<object, graph_link_presentation[]> outgoing_ports = new Dictionary<object, graph_link_presentation[]>();
		readonly Dictionary<object, graph_link_presentation[]> incoming_ports = new Dictionary<object, graph_link_presentation[]>();
		readonly Dictionary<graph_link_presentation, Point> port_fractions = new();
		horizontal_graph_layout? horizontal_layout;
		bool fit_neighborhood_on_update;
		readonly Dictionary<object, highlighted_snippet> node_snippets = new Dictionary<object, highlighted_snippet>();
		bool updating_visibility;
		double node_width = call_graph_surface.default_node_width;
		bool automatic_width = true;
		bool wrap_long_lines = true;
		bool syntax_highlighting = true;
		int card_preview_lines = 20;
		bool vertical_layout;
		object? selected_reference;
		object? root_reference;
		Point pan_start;
		double pan_horizontal_offset;
		double pan_vertical_offset;
		MouseButton pan_button;
		bool is_pan_candidate;
		bool is_panning;
		object? dragged_node_reference;
		Rect dragged_node_start;
		Point dragged_node_pointer_start;
		bool is_node_dragging;
		int render_version;
		Action? pending_render;
		readonly Dictionary<object, highlighted_snippet> pending_snippet_updates = new Dictionary<object, highlighted_snippet>();
		object? pending_root_key;
		(object key, Point screen)? deferred_anchor;
		bool anchor_restore_pending;
		int anchor_render_version;
		double anchor_zoom;
		internal void cancel_pending_render() {
			this.deferred_anchor = null;
			this.pending_render = null; this.pending_root_key = null; this.pending_snippet_updates.Clear();
			this.layout_cancellation?.Cancel();
			this.render_version++; this.layout_pending = false;
		}
		readonly Dictionary<object, Point> manual_positions = new Dictionary<object, Point>();
		static object reference_key(object reference) => reference is control_flow_block block ? (method: block.method, start_offset: block.start_offset) : reference;

		(object key, Point screen)? capture_anchor() {
			if (this.deferred_anchor is not null && this.anchor_render_version == this.render_version && this.anchor_zoom == this.zoom) return this.deferred_anchor;
			var viewport = this.visible_graph_rect();
			KeyValuePair<object, Rect> anchor = default; double distance = double.MaxValue;
			foreach (var pair in this.node_rects) {
				if (!pair.Value.IntersectsWith(viewport)) continue;
				double next = Math.Abs(pair.Value.Top - viewport.Top);
				if (Equals(pair.Key, this.selected_reference)) { anchor = pair; break; }
				if (next < distance) { anchor = pair; distance = next; }
			}
			if (anchor.Key is null) return null;
			return (call_graph_surface.reference_key(anchor.Key), new Point(this.pan_host.Padding.Left + anchor.Value.Left * this.zoom - this.scroll_viewer.HorizontalOffset,
				this.pan_host.Padding.Top + anchor.Value.Top * this.zoom - this.scroll_viewer.VerticalOffset));
		}

		void restore_anchor((object key, Point screen)? anchor) {
			if (anchor is null) return;
			this.deferred_anchor = anchor; this.anchor_render_version = this.render_version; this.anchor_zoom = this.zoom;
			if (this.anchor_restore_pending) return;
			this.anchor_restore_pending = true;
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				this.anchor_restore_pending = false; var restore = this.deferred_anchor; this.deferred_anchor = null;
				if (restore is null || this.anchor_render_version != this.render_version || this.anchor_zoom != this.zoom || this.is_pan_candidate) return;
				var match = this.node_rects.FirstOrDefault(pair => Equals(call_graph_surface.reference_key(pair.Key), restore.Value.key));
				if (match.Key is null) return;
				this.scroll_viewer.ScrollToHorizontalOffset(this.pan_host.Padding.Left + match.Value.Left * this.zoom - restore.Value.screen.X);
				this.scroll_viewer.ScrollToVerticalOffset(this.pan_host.Padding.Top + match.Value.Top * this.zoom - restore.Value.screen.Y);
			}));
		}

		public event EventHandler<graph_reference_event_args>? navigate_requested;
		public event EventHandler<graph_reference_event_args>? root_requested;
		public event EventHandler<graph_reference_event_args>? selection_changed;
		public event EventHandler? visible_methods_changed;

		public double zoom {
			get => this.scale_transform.ScaleX;
			set {
				double next = Math.Max(0.01, Math.Min(4.0, value));
				if (Math.Abs(next - this.scale_transform.ScaleX) < 0.001)
					return;
				this.scale_transform.ScaleX = next;
				this.deferred_anchor = null;
				this.scale_transform.ScaleY = next;
				this.update_extent();
			}
		}

		public call_graph_surface() : this(null) { }

		internal call_graph_surface(IThemeService? theme_service) {
			this.theme_service = theme_service;
			Focusable = true;
			this.canvas = new Canvas { MinWidth = 1, MinHeight = 1, ClipToBounds = false };
			this.edge_layer = new edge_layer_value(this);
			this.visibility_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(35) };
			this.visibility_timer.Tick += (_, __) => { this.visibility_timer.Stop(); this.update_visible_nodes(); };
			this.canvas.SetResourceReference(Panel.BackgroundProperty, "SystemColorsWindow");

			TextOptions.SetTextRenderingMode(this.canvas, TextRenderingMode.Grayscale);
			TextOptions.SetTextFormattingMode(this.canvas, TextFormattingMode.Ideal);
			RenderOptions.SetBitmapScalingMode(this.canvas, BitmapScalingMode.HighQuality);
			this.scale_transform = new ScaleTransform(1, 1);
			this.canvas.RenderTransform = this.scale_transform;
			this.extent_host = new Canvas { ClipToBounds = false };
			this.extent_host.Children.Add(this.canvas);
			this.pan_host = new Border {
				Child = this.extent_host,
				Padding = new Thickness(call_graph_surface.minimum_horizontal_pan_space, call_graph_surface.minimum_vertical_pan_space,
					call_graph_surface.minimum_horizontal_pan_space, call_graph_surface.minimum_vertical_pan_space),
			};
			this.pan_host.SetResourceReference(Border.BackgroundProperty, "SystemColorsWindow");
			this.scroll_viewer = new ScrollViewer {
				Content = this.pan_host,

				HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
				VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
				CanContentScroll = false,
				PanningMode = PanningMode.Both,
			};
			this.scroll_viewer.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow");
			this.scroll_viewer.SizeChanged += (_, __) => this.update_pan_space();
			this.scroll_viewer.ScrollChanged += (_, __) => { this.visibility_timer.Stop(); this.visibility_timer.Start(); };
			Content = this.scroll_viewer;

			PreviewMouseWheel += this.on_preview_mouse_wheel;
			PreviewMouseDown += this.on_preview_mouse_down;
			PreviewMouseMove += this.on_preview_mouse_move;
			PreviewMouseUp += this.on_preview_mouse_up;
			LostMouseCapture += (_, __) => this.end_pan();
			this.initialize_research_menu();
		}

		internal void configure(call_graph_settings settings) {
			if (this.node_width != settings.block_width || this.wrap_long_lines != settings.wrap_long_lines) this.measured_snippets.Clear();
			this.node_width = settings.block_width;
			this.wrap_long_lines = settings.wrap_long_lines;
			this.syntax_highlighting = settings.syntax_highlighting;
			this.card_preview_lines = settings.effective_maximum_lines;
		}

		internal void set_theme_service(IThemeService value) => this.theme_service = value;
		internal bool is_showing(MethodDef method) => !this.vertical_layout && ReferenceEquals(this.root_reference, method);
		void update_extent() {
			this.extent_host.Width = Math.Max(1, this.canvas.Width * this.zoom);
			this.extent_host.Height = Math.Max(1, this.canvas.Height * this.zoom);
			this.edge_layer.Width = Math.Max(1, this.canvas.Width);
			this.edge_layer.Height = Math.Max(1, this.canvas.Height);
		}

		void update_pan_space() {
			double width = this.scroll_viewer.ViewportWidth > 0 ? this.scroll_viewer.ViewportWidth : this.scroll_viewer.ActualWidth;
			double height = this.scroll_viewer.ViewportHeight > 0 ? this.scroll_viewer.ViewportHeight : this.scroll_viewer.ActualHeight;
			this.pan_host.Padding = new Thickness(
				Math.Max(call_graph_surface.minimum_horizontal_pan_space, width * 0.6),
				Math.Max(call_graph_surface.minimum_vertical_pan_space, height * 0.55),
				Math.Max(call_graph_surface.minimum_horizontal_pan_space, width * 0.6),
				Math.Max(call_graph_surface.minimum_vertical_pan_space, height * 0.55));
		}

		public void clear(string? message = null) {
			this.cancel_pending_render();
			this.incident_edges.Clear(); this.measured_snippets.Clear();
			this.edge_label_drawings.Clear();
			this.outgoing_ports.Clear(); this.incoming_ports.Clear(); this.port_fractions.Clear(); this.horizontal_layout = null;
			this.render_version++;
			this.end_pan();
			this.manual_positions.Clear();
			this.visibility_timer.Stop();
			this.edge_layer.invalidate_drawing();
			this.canvas.Children.Clear();
			this.node_borders.Clear();
			this.cached_borders.Clear();
			this.node_presentations.Clear();
			this.node_snippets.Clear();
			this.node_rects.Clear();
			this.edge_visuals.Clear();
			this.root_reference = this.selected_reference = null;
			this.canvas.Width = Math.Max(1, ActualWidth);
			this.canvas.Height = Math.Max(1, ActualHeight);
			this.update_extent();
			if (!string.IsNullOrWhiteSpace(message)) {
				var text = new TextBlock {
					Text = message,
					Margin = new Thickness(18),
					FontSize = 13,
				};
				text.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
				this.canvas.Children.Add(text);
				Canvas.SetLeft(text, 20);
				Canvas.SetTop(text, 20);
			}
		}

		public void render(call_graph_slice slice, IReadOnlyDictionary<MethodDef, highlighted_snippet> snippets) {
			int lines = this.card_preview_lines; double width = this.node_width; bool wrap = this.wrap_long_lines, automatic = this.automatic_width;
			var annotations = this.research_store is null ? null : slice.nodes.ToDictionary(node => node.method, node => this.research_store.get(node.method));
			var groups = new HashSet<string>(this.expanded_groups); var pinned = slice.nodes.Where(node => this.is_pinned(node.method)).Select(node => node.method).ToHashSet();
			call_graph_view prepare(CancellationToken token) {
				var previews = snippets.ToDictionary(pair => pair.Key, pair => {
					token.ThrowIfCancellationRequested(); var bounded = pair.Value.preview(lines, width, wrap);
					return automatic ? bounded.preview(lines, graph_layout.automatic_width(bounded.text, width), wrap) : bounded;
				});
				var view = call_graph_presentation.build(slice, previews, token);
				return annotations is null ? view : research_presentation.apply(view, method => annotations.GetValueOrDefault(method) ?? new research_annotation(), groups, pinned.Contains);
			}
			this.render_core(new graph_presentation(Array.Empty<graph_node_presentation>(), Array.Empty<graph_link_presentation>()), new Dictionary<object, highlighted_snippet>(),
				ui_text.pick("No calls were found for the selected method.", "Для выбранного метода вызовы не найдены."), false, prepare, slice.nodes.FirstOrDefault(node => node.is_root)?.method);
		}

		public void render(control_flow_graph graph, int maximum_lines = 0) {
			var snippets = graph.blocks.ToDictionary(block => (object)block, block => block.get_snippet(maximum_lines));
			var nodes = graph.blocks.Select(block => new graph_node_presentation(
				block, block.layer, block.is_entry, false,
				block.title,
				block.is_entry ? ui_text.pick("entry", "вход") + " • " + block.description : block.description,
				$"{call_graph_index.signature(block.method)}\n{block.title}–IL_{block.end_offset:X4}",
				snippets[block].text)).ToArray();
			var links = graph.edges.Select(edge => new graph_link_presentation(
				edge.source, edge.target, 1,
				$"{edge.source.title} → {edge.target.title}\n{edge.kind}" +
					(edge.label.Length == 0 ? string.Empty : ": " + edge.label),
				edge.label, edge.kind switch {
					control_flow_edge_kind.fall_through => graph_link_kind.fall_through,
					control_flow_edge_kind.conditional => graph_link_kind.conditional,
					control_flow_edge_kind.@switch => graph_link_kind.@switch,
					control_flow_edge_kind.exception => graph_link_kind.exception,
					_ => graph_link_kind.call,
				})).ToArray();
			this.render_core(new graph_presentation(nodes, links),
				snippets,
				ui_text.pick("The selected method does not have a managed IL body.", "У выбранного метода нет управляемого IL-тела."), true);
		}

		async void render_core(graph_presentation graph, IReadOnlyDictionary<object, highlighted_snippet> snippets,
			string empty_message, bool use_vertical_layout, Func<CancellationToken, call_graph_view>? prepare = null, object? prepared_root = null) {
			int version = ++this.render_version;
			this.layout_cancellation?.Cancel();
			if (this.is_pan_candidate) {
				var deferred_root = prepared_root ?? graph.nodes.FirstOrDefault(node => node.is_root)?.reference;
				var deferred_key = deferred_root is null ? null : call_graph_surface.reference_key(deferred_root);
				if (!Equals(this.pending_root_key, deferred_key)) this.pending_snippet_updates.Clear();
				this.pending_root_key = deferred_key;
				this.pending_render = () => {
					if (prepare is not null) { this.render_core(graph, snippets, empty_message, use_vertical_layout, prepare, prepared_root); return; }
					var latest = snippets.ToDictionary(pair => pair.Key, pair => pair.Value);
					foreach (var pair in this.pending_snippet_updates)
						if (latest.ContainsKey(pair.Key)) latest[pair.Key] = pair.Value;
					this.pending_snippet_updates.Clear();
					this.pending_root_key = null;
					this.render_core(graph, latest, empty_message, use_vertical_layout);
				};
				return;
			}
			this.layout_cancellation?.Dispose();
			this.layout_cancellation = new CancellationTokenSource();
			this.layout_pending = true;
			var token = this.layout_cancellation.Token;
			double width = this.node_width; bool wrap = this.wrap_long_lines, auto_width = this.automatic_width;
			graph_layout_result layout;
			try {
				var prepared = await Task.Run(() => {
					var view = prepare?.Invoke(token) ?? new call_graph_view(graph, snippets);
					return (view, layout: graph_layout.build(view.graph, view.snippets, width, wrap, use_vertical_layout, token, auto_width));
				}, token);
				graph = prepared.view.graph; snippets = prepared.view.snippets; layout = prepared.layout;
			}
			catch (OperationCanceledException) { return; }
			catch (Exception error) { if (version == this.render_version) this.clear(ui_text.render_failure(error.Message)); return; }
			if (version != this.render_version || token.IsCancellationRequested) return;
			if (this.is_pan_candidate) { this.pending_render = () => this.render_core(graph, snippets, empty_message, use_vertical_layout); return; }
			var next_root = graph.nodes.FirstOrDefault(node => node.is_root)?.reference;
			bool preserve = this.root_reference is not null && next_root is not null && this.vertical_layout == use_vertical_layout &&
				Equals(call_graph_surface.reference_key(this.root_reference), call_graph_surface.reference_key(next_root));
			var anchor = preserve ? this.capture_anchor() : null;
			if (!use_vertical_layout) layout = this.stabilize_layout(graph, layout, snippets, preserve);
			if (preserve) this.fit_neighborhood_on_update = false;
			this.save_positions();
			var selected_key = preserve && this.selected_reference is not null ? call_graph_surface.reference_key(this.selected_reference) : null;
			this.end_pan();
			if (!preserve) { this.manual_positions.Clear(); if (!use_vertical_layout) this.load_manual_positions(graph); }
			this.canvas.Children.Clear();
			this.node_borders.Clear();
			this.cached_borders.Clear();
			this.node_rects.Clear();
			this.edge_visuals.Clear();
			this.edge_label_drawings.Clear(); this.edge_layer.invalidate_drawing();
			this.incident_edges.Clear(); this.measured_snippets.Clear();
			this.node_presentations.Clear();
			this.node_snippets.Clear();
			this.root_reference = next_root;
			this.selected_reference = graph.nodes.FirstOrDefault(node => Equals(call_graph_surface.reference_key(node.reference), selected_key))?.reference ?? this.root_reference;
			this.vertical_layout = use_vertical_layout;
			if (!preserve) this.fit_neighborhood_on_update = !use_vertical_layout;

			if (graph.nodes.Count == 0) { this.clear(empty_message); return; }
			bool pending_geometry = false;
			foreach (var node in graph.nodes) {
				this.node_presentations[node.reference] = node;
				this.node_snippets[node.reference] = this.pending_snippet_updates.TryGetValue(node.reference, out var updated) ? updated :
					snippets.TryGetValue(node.reference, out var snippet) ? snippet : highlighted_snippet.plain(string.Empty);
				if (updated is not null && (!snippets.TryGetValue(node.reference, out var original) || original.text != updated.text || original.is_truncated != updated.is_truncated)) {
					var rect = layout.rectangles[node.reference]; rect.Width = this.automatic_width ? graph_layout.automatic_width(updated.text, this.node_width) : rect.Width;
					rect.Height = graph_layout.estimate_height(updated.text, rect.Width, this.wrap_long_lines) + (node.reference is MethodDef && updated.is_truncated ? 25 : 0); layout.rectangles[node.reference] = rect; pending_geometry = true;
				}
			}
			this.pending_snippet_updates.Clear();
			foreach (var pair in layout.rectangles) this.node_rects[pair.Key] = pair.Value;
			if (this.automatic_width) foreach (var reference in this.node_rects.Keys.ToArray()) { var rect = this.node_rects[reference]; rect.Width = graph_layout.automatic_width(this.node_snippets[reference].text, this.node_width); this.node_rects[reference] = rect; }
			this.canvas.Width = layout.extent.Width;
			this.canvas.Height = layout.extent.Height;
			foreach (var reference in this.node_rects.Keys.ToArray()) {
				if (!this.manual_positions.TryGetValue(call_graph_surface.reference_key(reference), out var position)) continue;
				var rect = this.node_rects[reference]; rect.Location = position; this.node_rects[reference] = rect;
			}
			var live_keys = new HashSet<object>(this.node_rects.Keys.Select(call_graph_surface.reference_key));
			foreach (var key in this.manual_positions.Keys.Where(key => !live_keys.Contains(key)).ToArray()) this.manual_positions.Remove(key);
			this.ensure_column_spacing(new HashSet<object>());
			this.canvas.Width = Math.Max(this.canvas.Width, this.node_rects.Values.Max(rect => rect.Right + call_graph_surface.canvas_margin));
			this.canvas.Height = Math.Max(this.canvas.Height, this.node_rects.Values.Max(rect => rect.Bottom + call_graph_surface.canvas_margin));
			this.update_extent();
			this.canvas.Children.Add(this.edge_layer);
			this.outgoing_ports.Clear(); this.incoming_ports.Clear();
			foreach (var group in graph.links.GroupBy(link => link.source)) this.outgoing_ports[group.Key] = group.ToArray();
			foreach (var group in graph.links.GroupBy(link => link.target)) this.incoming_ports[group.Key] = group.ToArray();
			this.port_fractions.Clear();
			foreach (var ports in this.outgoing_ports.Values) for (int port = 0; port < ports.Length; port++) this.port_fractions[ports[port]] = new Point((port + 1.0) / (ports.Length + 1.0), 0.5);
			foreach (var ports in this.incoming_ports.Values) for (int port = 0; port < ports.Length; port++) { var fraction = this.port_fractions[ports[port]]; fraction.Y = (port + 1.0) / (ports.Length + 1.0); this.port_fractions[ports[port]] = fraction; }
			this.horizontal_layout = layout.horizontal;
			if (this.manual_positions.Count != 0 || pending_geometry) this.compact_node_positions(new HashSet<object>());
			this.canvas.Height = Math.Max(420, this.node_rects.Values.Max(rect => rect.Bottom + call_graph_surface.canvas_margin)); this.update_extent();
			int edge_batch = 0; var batch_clock = Stopwatch.StartNew();
			foreach (var link in graph.links) {
				if (version != this.render_version || token.IsCancellationRequested) return;
				this.add_edge(link);
				if (++edge_batch >= 96 || batch_clock.ElapsedMilliseconds >= 4) { this.edge_layer.invalidate_drawing(true); await Dispatcher.Yield(DispatcherPriority.Background); batch_clock.Restart(); edge_batch = 0; }
			}
			this.edge_layer.invalidate_drawing(true);

			if (preserve) this.restore_anchor(anchor);
			this.layout_pending = false;
			_ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				if (version != this.render_version) return;
				if (!preserve && !this.is_pan_candidate) this.center_root();
				this.update_visible_nodes();
			}));
		}

		Rect visible_graph_rect() {
			double width = Math.Max(1, this.scroll_viewer.ViewportWidth);
			double height = Math.Max(1, this.scroll_viewer.ViewportHeight);
			var rect = new Rect((this.scroll_viewer.HorizontalOffset - this.pan_host.Padding.Left) / this.zoom,
				(this.scroll_viewer.VerticalOffset - this.pan_host.Padding.Top) / this.zoom, width / this.zoom, height / this.zoom);
			rect.Inflate(80 / this.zoom, 80 / this.zoom);
			return rect;
		}

		public IReadOnlyList<MethodDef> visible_methods() {
			var viewport = this.visible_graph_rect();
			return this.node_rects.Where(pair => pair.Key is MethodDef && pair.Value.IntersectsWith(viewport))
				.OrderBy(pair => Equals(pair.Key, this.root_reference) ? 0 : 1)
				.ThenBy(pair => Math.Abs(pair.Value.Top - viewport.Top))
				.Select(pair => (MethodDef)pair.Key).ToArray();
		}

		void update_visible_nodes() {
			if (this.updating_visibility || this.node_presentations.Count == 0) return;
			this.updating_visibility = true;
			try {
				var viewport = this.visible_graph_rect();
				var changed = new HashSet<object>(); var anchor = this.capture_anchor();
				foreach (var reference in this.node_borders.Keys.ToArray()) {
					if (Equals(reference, this.dragged_node_reference) || this.node_rects[reference].IntersectsWith(viewport)) continue;
					this.canvas.Children.Remove(this.node_borders[reference]);
					this.node_borders.Remove(reference);
				}
				int added = 0; var batch_clock = Stopwatch.StartNew();
				bool more = false;
				foreach (var reference in this.node_rects.Keys.ToArray()) {
					var pair = new KeyValuePair<object, Rect>(reference, this.node_rects[reference]);
					if (this.node_borders.ContainsKey(pair.Key) || !pair.Value.IntersectsWith(viewport)) continue;
					if (added >= 12 || added != 0 && batch_clock.ElapsedMilliseconds >= 4) { more = true; break; }
					added++;
					if (this.cached_borders.TryGetValue(pair.Key, out var cached)) { this.attach_node(this.node_presentations[pair.Key], cached); continue; }
					var snippet = this.node_snippets[pair.Key]; var code = this.create_code_text_block(snippet);
					if (!this.measured_snippets.TryGetValue(pair.Key, out var measured) || !ReferenceEquals(measured, snippet)) {
						this.resize_node(pair.Key, this.measure_node_height(code, this.node_rects[pair.Key].Width) + (pair.Key is MethodDef && snippet.is_truncated ? 25 : 0), changed); this.measured_snippets[pair.Key] = snippet;
					}
					this.add_node(this.node_presentations[pair.Key], code);
				}
				this.apply_geometry_changes(changed); if (changed.Count != 0) this.restore_anchor(anchor);
				more |= changed.Count != 0;
				if (more && !this.visibility_update_pending) {
					this.visibility_update_pending = true;
					Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { this.visibility_update_pending = false; this.update_visible_nodes(); }));
				}
			}
			finally { this.updating_visibility = false; }
			this.visible_methods_changed?.Invoke(this, EventArgs.Empty);
		}

		void resize_node(object reference, double height, HashSet<object> changed) {
			var old = this.node_rects[reference]; double delta = height - old.Height;
			if (Math.Abs(delta) < 0.5) return;
			int layer = this.node_presentations[reference].layer;
			double previous_bottom = this.vertical_layout ? this.node_rects.Where(pair => this.node_presentations[pair.Key].layer == layer).Max(pair => pair.Value.Bottom) : 0;
			this.node_rects[reference] = new Rect(old.Location, new Size(old.Width, height)); changed.Add(reference);
			if (this.cached_borders.TryGetValue(reference, out var resized)) resized.Height = height;
			if (!this.vertical_layout && this.dragged_node_reference is null) return;
			if (this.vertical_layout) delta = this.node_rects.Where(pair => this.node_presentations[pair.Key].layer == layer).Max(pair => pair.Value.Bottom) - previous_bottom;
			foreach (var other in this.node_rects.Keys.ToArray()) {
				if (Equals(other, reference) || Equals(other, this.dragged_node_reference) || this.manual_positions.ContainsKey(call_graph_surface.reference_key(other))) continue;
				var rect = this.node_rects[other];
				if (this.vertical_layout ? this.node_presentations[other].layer <= layer : Math.Abs(rect.Left - old.Left) > 0.5 || rect.Top <= old.Top) continue;
				rect.Y += delta; this.node_rects[other] = rect; changed.Add(other);
				if (this.node_borders.TryGetValue(other, out var moved)) Canvas.SetTop(moved, rect.Top);
			}
		}
		void apply_geometry_changes(HashSet<object> changed) {
			if (changed.Count == 0) return;
			this.ensure_column_spacing(changed); this.compact_node_positions(changed);
			this.canvas.Width = Math.Max(640, this.node_rects.Values.Select(rect => rect.Right + call_graph_surface.canvas_margin).DefaultIfEmpty(640).Max());
			this.canvas.Height = Math.Max(420, this.node_rects.Values.Select(rect => rect.Bottom + call_graph_surface.canvas_margin).DefaultIfEmpty(420).Max());
			this.update_extent();
			var affected = new HashSet<edge_visual>();
			foreach (var reference in changed) if (this.incident_edges.TryGetValue(reference, out var edges)) affected.UnionWith(edges);
			foreach (var edge in affected) this.update_edge(edge);
			this.edge_layer.invalidate_drawing();
		}
		public void update_snippets(IReadOnlyDictionary<MethodDef, highlighted_snippet> snippets) {
			var anchor = this.capture_anchor(); var viewport = this.visible_graph_rect(); var changed = new HashSet<object>();
			foreach (var pair in snippets) {
				if (this.research_store?.get(pair.Key).compact == true) continue;
				var preview = this.prepare_card_snippet(pair.Value);
				if (this.pending_render is not null || this.layout_pending) this.pending_snippet_updates[pair.Key] = preview;
				if (!this.node_rects.TryGetValue(pair.Key, out var rect)) continue;
				if (this.node_snippets.TryGetValue(pair.Key, out var previous) && previous.text == preview.text && previous.is_truncated == preview.is_truncated) continue;
				this.node_snippets[pair.Key] = preview; this.measured_snippets.Remove(pair.Key);
				if (this.automatic_width) this.resize_node_width(pair.Key, graph_layout.automatic_width(preview.text, this.node_width), changed);
				if (!rect.IntersectsWith(viewport)) { this.resize_node(pair.Key, graph_layout.estimate_height(preview.text, this.node_rects[pair.Key].Width, this.wrap_long_lines) + (preview.is_truncated ? 25 : 0), changed); this.cached_borders.Remove(pair.Key); continue; }
				var code = this.create_code_text_block(preview); this.resize_node(pair.Key, this.measure_node_height(code, this.node_rects[pair.Key].Width) + (preview.is_truncated ? 25 : 0), changed);
				this.measured_snippets[pair.Key] = preview;
				if (this.cached_borders.TryGetValue(pair.Key, out var border)) {
					var grid = (Grid)border.Child; foreach (var previous_code in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == 2).ToArray()) grid.Children.Remove(previous_code); Grid.SetRow(code, 2); grid.Children.Add(code);
					this.update_source_footer(grid, pair.Key, preview);
				}
			}
			this.apply_geometry_changes(changed); this.restore_anchor(anchor);
			if (this.fit_neighborhood_on_update) this.fit_root_neighborhood();
			this.update_visible_nodes();
		}
		public void render(call_graph_slice slice, IReadOnlyDictionary<MethodDef, string> snippets) =>
			this.render(slice, snippets.ToDictionary(pair => pair.Key, pair => highlighted_snippet.plain(pair.Value)));

		highlighted_snippet prepare_card_snippet(highlighted_snippet snippet) {
			var bounded = snippet.preview(this.card_preview_lines, this.node_width, this.wrap_long_lines);
			return this.automatic_width ? bounded.preview(this.card_preview_lines, graph_layout.automatic_width(bounded.text, this.node_width), this.wrap_long_lines) : bounded;
		}
		double measure_node_height(FrameworkElement text, double width) {
			double available_width = Math.Max(1, width - 4);
			text.Measure(new Size(available_width, double.PositiveInfinity));
			return Math.Max(call_graph_surface.minimum_node_height,
				Math.Ceiling(call_graph_surface.header_height + 1 + text.DesiredSize.Height + 4));
		}
		void resize_node_width(object reference, double width, HashSet<object> changed) {
			var rect = this.node_rects[reference]; if (Math.Abs(rect.Width - width) < 0.5) return;
			rect.Width = width; this.node_rects[reference] = rect; changed.Add(reference);
			if (this.cached_borders.TryGetValue(reference, out var border)) border.Width = width;
		}
		void ensure_column_spacing(HashSet<object> changed) {
			if (this.vertical_layout || !this.automatic_width) return;
			double right = double.NegativeInfinity;
			foreach (var group in this.node_rects.Keys.GroupBy(reference => this.node_presentations[reference].layer).OrderBy(group => group.Key)) {
				double left = group.Min(reference => this.node_rects[reference].Left), shift = Math.Max(0, right + 115 - left);
				if (shift > 0.5) foreach (var reference in group) {
					var rect = this.node_rects[reference]; rect.X += shift; this.node_rects[reference] = rect; changed.Add(reference);
					if (this.manual_positions.ContainsKey(call_graph_surface.reference_key(reference))) this.manual_positions[call_graph_surface.reference_key(reference)] = rect.Location;
					if (this.node_borders.TryGetValue(reference, out var border)) Canvas.SetLeft(border, rect.Left);
				}
				right = group.Max(reference => this.node_rects[reference].Right);
			}
		}

		void add_edge(graph_link_presentation link) {
			if (!this.try_get_edge_placement(link, out var placement))
				return;
			bool emphasized = Equals(link.source, this.root_reference) || Equals(link.target, this.root_reference);
			var path = call_graph_surface.create_curve_geometry(placement);
			var arrow = call_graph_surface.create_arrow(placement.to, placement.control2, emphasized);
			string? label = null;
			Vector label_offset = default;
			if (link.call_site_count > 1) {
				label = "×" + link.call_site_count.ToString(CultureInfo.InvariantCulture);
				label_offset = new Vector(-9, -9);
			}
			else if (!string.IsNullOrEmpty(link.label)) {
				label = link.label;
				label_offset = new Vector(3, -10);
			}
			var visual = new edge_visual(link, path, arrow, label, link.call_site_count > 1 ? 10 : 9.5, label_offset, placement.label_point + label_offset);
			this.edge_visuals.Add(visual);
			foreach (var reference in new[] { link.source, link.target }.Distinct()) {
				if (!this.incident_edges.TryGetValue(reference, out var connected)) this.incident_edges[reference] = connected = new List<edge_visual>();
				connected.Add(visual);
			}
		}

		void draw_edges(DrawingContext context, int start = 0) {
			var accent = (TryFindResource("HyperlinkNormal") as Brush ?? Brushes.DeepSkyBlue).CloneCurrentValue();
			var text = TryFindResource("SystemColorsWindowText") as Brush ?? Brushes.Gainsboro;
			var normal = text.CloneCurrentValue(); normal.Opacity = 0.52; accent.Opacity = 0.82;
			var background = TryFindResource("SystemColorsWindow") as Brush ?? Brushes.Black;
			var normal_pen = new Pen(normal, 1.15);
			var accent_pen = new Pen(accent, 1.8);
			var dashed_pen = new Pen(accent, 1.3) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
			var address_pen = new Pen(Brushes.MediumPurple, 1.3) { DashStyle = new DashStyle(new[] { 1.0, 4.0 }, 0) };
			for (int index = start; index < this.edge_visuals.Count; index++) {
				var edge = this.edge_visuals[index];
				bool emphasized = Equals(edge.link.source, this.root_reference) || Equals(edge.link.target, this.root_reference) || edge.link.kind == graph_link_kind.conditional || edge.link.kind == graph_link_kind.@switch;
				bool dashed = edge.link.kind is graph_link_kind.possible_call or graph_link_kind.exception or graph_link_kind.address_reference or graph_link_kind.structural_call or graph_link_kind.value_dependency or graph_link_kind.initialization_dependency;
				context.DrawGeometry(null, edge.link.kind == graph_link_kind.address_reference ? address_pen : dashed ? dashed_pen : emphasized ? accent_pen : normal_pen, edge.curve);
				context.DrawGeometry(edge.link.kind == graph_link_kind.address_reference ? Brushes.MediumPurple : emphasized || dashed ? accent : normal, null, edge.arrow);
				if (edge.label is null) continue;
				var key = (label: edge.label, label_size: edge.label_size, pixels_per_dip: VisualTreeHelper.GetDpi(this).PixelsPerDip);
				if (!this.edge_label_drawings.TryGetValue(key, out var label)) {
					if (this.edge_label_drawings.Count >= 256) this.edge_label_drawings.Clear();
					var formatted = new FormattedText(edge.label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), edge.label_size, text, key.Item3);
					label = new DrawingGroup(); using (var target = label.Open()) { target.DrawRectangle(background, null, new Rect(new Point(), new Size(formatted.Width + 3, formatted.Height))); target.DrawText(formatted, new Point()); }
					this.edge_label_drawings.Add(key, label);
				}
				context.PushTransform(new TranslateTransform(edge.label_point.X, edge.label_point.Y)); context.DrawDrawing(label); context.Pop();
			}
		}

		bool try_get_edge_placement(graph_link_presentation link, out edge_placement placement) {
			placement = default;
			if (!this.node_rects.TryGetValue(link.source, out var source_rect) ||
				!this.node_rects.TryGetValue(link.target, out var target_rect))
				return false;
			Point from;
			Point to;
			Point control1;
			Point control2;
			Point label_point;
			double port(bool source) {
				return this.port_fractions.TryGetValue(link, out var fraction) ? source ? fraction.X : fraction.Y : 0.5;
			}
			if (Equals(link.source, link.target)) {
				from = new Point(source_rect.Right, source_rect.Top + source_rect.Height * 0.3);
				to = new Point(source_rect.Right, source_rect.Top + source_rect.Height * 0.7);
				control1 = new Point(from.X + 80, from.Y); control2 = new Point(to.X + 80, to.Y);
				label_point = new Point(from.X + 65, (from.Y + to.Y) / 2);
			}
			else if (this.vertical_layout && target_rect.Top >= source_rect.Bottom) {
				from = new Point(source_rect.Left + source_rect.Width / 2, source_rect.Bottom);
				to = new Point(target_rect.Left + target_rect.Width / 2, target_rect.Top);
				double delta_y = Math.Max(38, Math.Abs(to.Y - from.Y) * 0.45);
				control1 = new Point(from.X, from.Y + delta_y);
				control2 = new Point(to.X, to.Y - delta_y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (this.vertical_layout && source_rect.Top >= target_rect.Bottom) {
				from = new Point(source_rect.Left + source_rect.Width / 2, source_rect.Top);
				to = new Point(target_rect.Left + target_rect.Width / 2, target_rect.Bottom);
				double delta_y = Math.Max(38, Math.Abs(to.Y - from.Y) * 0.45);
				control1 = new Point(from.X, from.Y - delta_y);
				control2 = new Point(to.X, to.Y + delta_y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (target_rect.Left >= source_rect.Right) {
				from = new Point(source_rect.Right, source_rect.Top + source_rect.Height * port(true));
				to = new Point(target_rect.Left, target_rect.Top + target_rect.Height * port(false));
				double delta_x = Math.Max(45, Math.Abs(to.X - from.X) * 0.45);
				control1 = new Point(from.X + delta_x, from.Y);
				control2 = new Point(to.X - delta_x, to.Y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (source_rect.Left >= target_rect.Right) {
				from = new Point(source_rect.Left, source_rect.Top + source_rect.Height * port(true));
				to = new Point(target_rect.Right, target_rect.Top + target_rect.Height * port(false));
				double delta_x = Math.Max(45, Math.Abs(to.X - from.X) * 0.45);
				control1 = new Point(from.X - delta_x, from.Y);
				control2 = new Point(to.X + delta_x, to.Y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else {
				from = new Point(source_rect.Right, source_rect.Top + source_rect.Height / 2);
				to = new Point(target_rect.Right, target_rect.Top + target_rect.Height / 2);
				double control_x = Math.Max(source_rect.Right, target_rect.Right) + 50;
				control1 = new Point(control_x, from.Y);
				control2 = new Point(control_x, to.Y);
				label_point = new Point(control_x, (from.Y + to.Y) / 2);
			}
			placement = new edge_placement(from, to, control1, control2, label_point);
			return true;
		}

		static Geometry create_curve_geometry(edge_placement placement) {
			var geometry = new StreamGeometry();
			using (var context = geometry.Open()) { context.BeginFigure(placement.from, false, false); context.BezierTo(placement.control1, placement.control2, placement.to, true, false); }
			geometry.Freeze(); return geometry;
		}

		static Geometry create_arrow(Point tip, Point from, bool emphasized) {
			Vector vector = tip - from;
			if (vector.LengthSquared >= 1)
				vector.Normalize();
			else
				vector = new Vector(1, 0);
			Vector perpendicular = new Vector(-vector.Y, vector.X);
			double length = emphasized ? 10 : 8;
			double width = emphasized ? 4.8 : 4;
			Point base_point = tip - vector * length;
			var geometry = new StreamGeometry();
			using (var context = geometry.Open()) {
				context.BeginFigure(tip, true, true);
				context.LineTo(base_point + perpendicular * width, true, false);
				context.LineTo(base_point - perpendicular * width, true, false);
			}
			geometry.Freeze();
			return geometry;
		}

		void update_connected_edges(object reference) {
			if (this.incident_edges.TryGetValue(reference, out var connected)) foreach (var visual in connected) this.update_edge(visual);
			this.edge_layer.invalidate_drawing();
		}

		void update_edge(edge_visual visual) {
			if (!this.try_get_edge_placement(visual.link, out var placement)) return;
			visual.curve = call_graph_surface.create_curve_geometry(placement);
			visual.arrow = call_graph_surface.create_arrow(placement.to, placement.control2,
				Equals(visual.link.source, this.root_reference) || Equals(visual.link.target, this.root_reference));
			visual.label_point = placement.label_point + visual.label_offset;
		}

		void add_node(graph_node_presentation node, graph_code_text code_text) {
			Rect rect = this.node_rects[node.reference];
			if (this.cached_borders.TryGetValue(node.reference, out var cached)) {
				this.attach_node(node, cached); return;
			}
			var border = new Border {
				Width = rect.Width,
				Height = rect.Height,
				BorderThickness = node.is_root ? new Thickness(2) : new Thickness(1),
				CornerRadius = new CornerRadius(2),
				SnapsToDevicePixels = true,
				Tag = node.reference,
				ToolTip = node.tool_tip,
			};
			border.SetResourceReference(Border.BackgroundProperty, "SystemColorsWindow");
			border.SetResourceReference(Border.BorderBrushProperty,
				Equals(node.reference, this.selected_reference) && !node.is_root ? "ToolBarButtonHoverBorder" :
				node.is_root ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");

			var grid = new Grid();
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.Children.Add(this.create_header(node));

			var separator = new Border { Height = 1 };
			separator.SetResourceReference(Border.BackgroundProperty, "CommonControlsTextBoxBorder");
			Grid.SetRow(separator, 1);
			grid.Children.Add(separator);

			Grid.SetRow(code_text, 2);
			grid.Children.Add(code_text);
			this.update_source_footer(grid, node.reference, this.node_snippets[node.reference]);
			border.Child = grid;

			border.MouseLeftButtonDown += this.on_node_mouse_left_button_down;
			var lazy_menu = new ContextMenu();
			bool menu_created = false;
			lazy_menu.Opened += (_, __) => {
				if (menu_created) return;
				menu_created = true;
				var populated = this.create_node_context_menu(node);
				while (populated.Items.Count != 0) { var item = populated.Items[0]; populated.Items.RemoveAt(0); lazy_menu.Items.Add(item); }
			};
			border.ContextMenu = lazy_menu;
			this.node_borders[node.reference] = border;
			this.cached_borders[node.reference] = border;
			Panel.SetZIndex(border, 2);
			this.canvas.Children.Add(border);
			Canvas.SetLeft(border, rect.Left);
			Canvas.SetTop(border, rect.Top);
		}
		void attach_node(graph_node_presentation node, Border border) {
			var rect = this.node_rects[node.reference]; this.node_borders[node.reference] = border;
			border.BorderThickness = Equals(this.selected_reference, node.reference) || node.is_root ? new Thickness(2) : new Thickness(1);
			border.SetResourceReference(Border.BorderBrushProperty, Equals(this.selected_reference, node.reference) && !node.is_root ? "ToolBarButtonHoverBorder" : node.is_root ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");
			this.canvas.Children.Add(border); Canvas.SetLeft(border, rect.Left); Canvas.SetTop(border, rect.Top);
		}

		graph_code_text create_code_text_block(highlighted_snippet code) => new graph_code_text(code, this.theme_service,
			this.syntax_highlighting, this.wrap_long_lines, reference => {
				if (call_graph_control.resolve_method(reference) is MethodDef method)
					this.root_requested?.Invoke(this, new graph_reference_event_args(method));
			}, this.create_method_reference_context_menu);

		FrameworkElement create_header(graph_node_presentation node) {
			var panel = new Grid { MinHeight = 47 };
			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.SetResourceReference(Panel.BackgroundProperty,
				node.is_root ? "ToolBarButtonChecked" : "EnvironmentBackground");

			var name = new TextBlock {
				Text = node.title,
				FontWeight = FontWeights.SemiBold,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(8, 5, 8, 0),
			};
			name.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			if (this.theme_service is not null && node.reference is MethodDef method) {
				var color = this.theme_service.Theme.GetTextColor((ColorType)((uint)ColorType.Text + (uint)(method.IsStatic ? TextColor.StaticMethod : TextColor.InstanceMethod)));
				if (color.Foreground is not null) name.Foreground = color.Foreground;
			}
			panel.Children.Add(name);

			string subtitle = node.subtitle;
			if (node.is_external)
				subtitle += "   " + ui_text.pick("[external]", "[внешний]");
			var type = new TextBlock {
				Text = subtitle,
				FontSize = 10.5,
				Opacity = 0.75,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(8, 0, 8, 5),
			};
			type.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			Grid.SetRow(type, 1);
			panel.Children.Add(type);
			if (node.continuation.Length != 0) {
				var expand = new Button { Content = "+", Width = 23, Height = 21, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 5, 0), ToolTip = node.continuation };
				expand.Click += (_, event_args) => { this.request(node.reference, node.reference is research_group ? research_action.expand_group : node.continuation_shows_evidence ? research_action.evidence : research_action.expand); event_args.Handled = true; };
				name.Margin = new Thickness(8, 5, 33, 0); Grid.SetRowSpan(expand, 2); panel.Children.Add(expand);
			}
			return panel;
		}

		ContextMenu create_node_context_menu(graph_node_presentation node) {
			var menu = new ContextMenu();
			if (node.reference is control_flow_block block) {
				var open_method = new MenuItem { Header = ui_text.open_method_source, FontWeight = FontWeights.SemiBold };
				open_method.Click += (_, __) => this.navigate_requested?.Invoke(this, new graph_reference_event_args(block.method));
				menu.Items.Add(open_method);
				menu.Items.Add(new Separator());
				var copy_block = new MenuItem { Header = ui_text.copy_block_il };
				copy_block.Click += (_, __) => Clipboard.SetText(block.snippet_value.text);
				menu.Items.Add(copy_block);
				return menu;
			}
			this.add_research_menu(menu, node.reference);
			var root = new MenuItem { Header = ui_text.explore_here, InputGestureText = call_graph_command_loader.shortcut_display_text, FontWeight = FontWeights.SemiBold, IsEnabled = node.reference is MethodDef };
			root.Click += (_, __) => this.root_requested?.Invoke(this, new graph_reference_event_args(node.reference));
			menu.Items.Add(root);
			var open = new MenuItem { Header = ui_text.open_source };
			open.Click += (_, __) => this.navigate_requested?.Invoke(this, new graph_reference_event_args(node.reference is unresolved_call_target unknown ? unknown.source : node.reference));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = ui_text.copy_full_name };
			copy.Click += (_, __) => Clipboard.SetText(node.copy_text);
			menu.Items.Add(copy);
			return menu;
		}

		ContextMenu create_method_reference_context_menu(IMethod reference) {
			var menu = new ContextMenu();
			var explore = new MenuItem {
				Header = this.vertical_layout ? ui_text.show_method_in_cfg : ui_text.explore_here,
				FontWeight = FontWeights.SemiBold,
			};
			explore.Click += (_, __) => {
				if (call_graph_control.resolve_method(reference) is MethodDef definition)
					this.root_requested?.Invoke(this, new graph_reference_event_args(definition));
			};
			menu.Items.Add(explore);
			var open = new MenuItem { Header = ui_text.open_source };
			open.Click += (_, __) => this.navigate_requested?.Invoke(this, new graph_reference_event_args(reference));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = ui_text.copy_full_name };
			copy.Click += (_, __) => Clipboard.SetText(reference.FullName);
			menu.Items.Add(copy);
			return menu;
		}

		void on_node_mouse_left_button_down(object sender, MouseButtonEventArgs e) {
			if (sender is not Border border || border.Tag is not object reference)
				return;
			if (e.ClickCount == 2 && reference is not control_flow_block) {
				this.root_requested?.Invoke(this, new graph_reference_event_args(reference));
				e.Handled = true;
				return;
			}
			this.select_node(reference);
			e.Handled = false;
		}

		void select_node(object reference) {
			if (Equals(this.selected_reference, reference))
				return;
			if (this.selected_reference is not null && this.node_borders.TryGetValue(this.selected_reference, out var old_border)) {
				old_border.BorderThickness = Equals(old_border.Tag, this.root_reference) ? new Thickness(2) : new Thickness(1);
				old_border.SetResourceReference(Border.BorderBrushProperty,
					Equals(old_border.Tag, this.root_reference) ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");
			}
			this.selected_reference = reference;
			if (this.node_borders.TryGetValue(reference, out var border)) {
				border.BorderThickness = new Thickness(2);
				border.SetResourceReference(Border.BorderBrushProperty, "ToolBarButtonHoverBorder");
			}
			this.selection_changed?.Invoke(this, new graph_reference_event_args(reference));
		}

		public void center_root() {
			this.deferred_anchor = null;
			if (this.root_reference is null)
				return;
			if (!this.vertical_layout) this.fit_root_neighborhood(); else this.center_reference(this.root_reference);
			this.update_visible_nodes();
		}
		void fit_root_neighborhood() {
			if (this.root_reference is null || !this.node_rects.TryGetValue(this.root_reference, out var bounds) || this.scroll_viewer.ViewportWidth < 10 || this.scroll_viewer.ViewportHeight < 10) return;
			if (this.outgoing_ports.TryGetValue(this.root_reference, out var links)) foreach (var link in links) if (this.node_rects.TryGetValue(link.target, out var target)) bounds.Union(target);
			if (this.incoming_ports.TryGetValue(this.root_reference, out var callers)) foreach (var link in callers) if (this.node_rects.TryGetValue(link.source, out var source)) bounds.Union(source);
			this.zoom = Math.Clamp(Math.Min((this.scroll_viewer.ViewportWidth - 60) / Math.Max(1, bounds.Width), (this.scroll_viewer.ViewportHeight - 60) / Math.Max(1, bounds.Height)), 0.2, 1);
			this.scroll_viewer.UpdateLayout();
			this.scroll_viewer.ScrollToHorizontalOffset(this.pan_host.Padding.Left + (bounds.Left + bounds.Width / 2) * this.zoom - this.scroll_viewer.ViewportWidth / 2);
			this.scroll_viewer.ScrollToVerticalOffset(this.pan_host.Padding.Top + (bounds.Top + bounds.Height / 2) * this.zoom - this.scroll_viewer.ViewportHeight / 2);
		}

		public bool focus_reference(object reference) {
			if (!this.node_rects.ContainsKey(reference))
				return false;
			this.select_node(reference);
			this.center_reference(reference);
			return true;
		}

		void center_reference(object reference) {
			this.deferred_anchor = null;
			if (!this.node_rects.TryGetValue(reference, out var rect))
				return;
			double center_x = this.pan_host.Padding.Left + (rect.Left + rect.Width / 2) * this.zoom;
			double center_y = this.pan_host.Padding.Top + (rect.Top + rect.Height / 2) * this.zoom;
			this.scroll_viewer.ScrollToHorizontalOffset(center_x - this.scroll_viewer.ViewportWidth / 2);
			this.scroll_viewer.ScrollToVerticalOffset(center_y - this.scroll_viewer.ViewportHeight / 2);
		}

		void on_preview_mouse_wheel(object sender, MouseWheelEventArgs e) {
			this.fit_neighborhood_on_update = false;
			if (e.Delta == 0)
				return;

			Point pointer = e.GetPosition(this.scroll_viewer);
			double old_zoom = this.zoom;
			double content_x = (this.scroll_viewer.HorizontalOffset + pointer.X - this.pan_host.Padding.Left) / old_zoom;
			double content_y = (this.scroll_viewer.VerticalOffset + pointer.Y - this.pan_host.Padding.Top) / old_zoom;
			double wheel_notches = e.Delta / 120.0;
			this.zoom = old_zoom * Math.Pow(1.18, wheel_notches);
			double new_zoom = this.zoom;

			this.scroll_viewer.UpdateLayout();
			this.scroll_viewer.ScrollToHorizontalOffset(this.pan_host.Padding.Left + content_x * new_zoom - pointer.X);
			this.scroll_viewer.ScrollToVerticalOffset(this.pan_host.Padding.Top + content_y * new_zoom - pointer.Y);
			e.Handled = true;
		}

		void on_preview_mouse_down(object sender, MouseButtonEventArgs e) {
			this.fit_neighborhood_on_update = false;
			if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle)
				return;
			this.pan_button = e.ChangedButton;
			this.deferred_anchor = null;
			this.is_pan_candidate = true;
			this.is_panning = false;
			this.is_node_dragging = false;
			this.pan_start = e.GetPosition(this);
			this.pan_horizontal_offset = this.scroll_viewer.HorizontalOffset;
			this.pan_vertical_offset = this.scroll_viewer.VerticalOffset;
			this.dragged_node_reference = e.ChangedButton == MouseButton.Left && e.ClickCount == 1
				? this.find_node_reference(e.OriginalSource as DependencyObject)
				: null;
			if (this.dragged_node_reference is not null && this.node_rects.TryGetValue(this.dragged_node_reference, out var rect)) {
				this.dragged_node_start = rect;
				this.dragged_node_pointer_start = e.GetPosition(this.canvas);
			}
			if (this.pan_button == MouseButton.Middle)
				e.Handled = true;
		}

		void on_preview_mouse_move(object sender, MouseEventArgs e) {
			if (!this.is_pan_candidate)
				return;
			bool button_pressed = this.pan_button == MouseButton.Left
				? e.LeftButton == MouseButtonState.Pressed
				: e.MiddleButton == MouseButtonState.Pressed;
			if (!button_pressed) {
				this.end_pan();
				return;
			}
			Point current = e.GetPosition(this);
			if (this.dragged_node_reference is not null) {
				Vector screen_delta = current - this.pan_start;
				if (!this.is_node_dragging && Math.Abs(screen_delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
					Math.Abs(screen_delta.Y) < SystemParameters.MinimumVerticalDragDistance)
					return;
				if (!this.is_node_dragging) {
					this.is_node_dragging = true;
					Mouse.Capture(this);
					Cursor = Cursors.SizeAll;
					if (this.node_borders.TryGetValue(this.dragged_node_reference, out var dragged_border))
						Panel.SetZIndex(dragged_border, 4);
				}
				Point canvas_point = e.GetPosition(this.canvas);
				Vector delta = canvas_point - this.dragged_node_pointer_start;
				this.move_node(this.dragged_node_reference,
					Math.Max(0, this.dragged_node_start.Left + delta.X),
					Math.Max(0, this.dragged_node_start.Top + delta.Y));
				e.Handled = true;
				return;
			}
			if (!this.is_panning) {
				Vector delta = current - this.pan_start;
				if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
					Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
					return;
				this.is_panning = true;
				Mouse.Capture(this);
				Cursor = Cursors.ScrollAll;
			}
			this.scroll_viewer.ScrollToHorizontalOffset(this.pan_horizontal_offset - (current.X - this.pan_start.X));
			this.scroll_viewer.ScrollToVerticalOffset(this.pan_vertical_offset - (current.Y - this.pan_start.Y));
			e.Handled = true;
		}

		void on_preview_mouse_up(object sender, MouseButtonEventArgs e) {
			if (!this.is_pan_candidate || e.ChangedButton != this.pan_button)
				return;
			bool handled = this.is_panning || this.is_node_dragging || this.pan_button == MouseButton.Middle;
			this.end_pan();
			e.Handled = handled;
		}

		void end_pan() {
			bool had_capture = IsMouseCaptured;
			if (this.dragged_node_reference is not null && this.node_borders.TryGetValue(this.dragged_node_reference, out var dragged_border))
				Panel.SetZIndex(dragged_border, 2);
			this.is_pan_candidate = false;
			this.is_panning = false;
			this.is_node_dragging = false;
			this.dragged_node_reference = null;
			Cursor = Cursors.Arrow;
			if (had_capture)
				Mouse.Capture(null);
			var pending = this.pending_render;
			this.pending_render = null;
			pending?.Invoke();
		}

		object? find_node_reference(DependencyObject? source) {
			var current = source;
			while (current is not null) {
				if (current is Border border && border.Tag is not null &&
					this.node_borders.TryGetValue(border.Tag, out var node_border) && ReferenceEquals(border, node_border))
					return border.Tag;
				if (current is FrameworkContentElement content) {
					current = content.Parent;
					continue;
				}
				if (current is FrameworkElement element && element.Parent is not null) {
					current = element.Parent;
					continue;
				}
				try { current = VisualTreeHelper.GetParent(current); }
				catch (InvalidOperationException) { current = null; }
			}
			return null;
		}
		internal MethodDef? resolve_shortcut_method(DependencyObject? pointer) => (this.find_node_reference(pointer) ?? this.selected_reference) switch {
			MethodDef method => method,
			control_flow_block block => block.method,
			_ => null,
		};
		internal MethodDef? shortcut_method => this.resolve_shortcut_method(IsMouseOver ? Mouse.DirectlyOver as DependencyObject : null);

		void move_node(object reference, double left, double top) {
			this.manual_positions[call_graph_surface.reference_key(reference)] = new Point(left, top);
			if (!this.node_rects.TryGetValue(reference, out var old_rect) || !this.node_borders.TryGetValue(reference, out var border))
				return;
			var rect = new Rect(left, top, old_rect.Width, old_rect.Height);
			this.node_rects[reference] = rect;
			Canvas.SetLeft(border, rect.Left);
			Canvas.SetTop(border, rect.Top);
			this.canvas.Width = Math.Max(this.canvas.Width, rect.Right + call_graph_surface.canvas_margin);
			this.canvas.Height = Math.Max(this.canvas.Height, rect.Bottom + call_graph_surface.canvas_margin);
			this.update_extent();
			this.update_connected_edges(reference);
		}
	}
}
