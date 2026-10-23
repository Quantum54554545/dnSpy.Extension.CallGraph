using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
	sealed class FunctionMapSurface : FrameworkElement {
		sealed class GraphVisual : FrameworkElement {
			readonly DrawingVisual drawing = new DrawingVisual();
			public GraphVisual() { AddVisualChild(drawing); IsHitTestVisible = false; }
			public DrawingContext RenderOpen() => drawing.RenderOpen();
			protected override int VisualChildrenCount => 1;
			protected override Visual GetVisualChild(int index) => index == 0 ? drawing : throw new ArgumentOutOfRangeException(nameof(index));
		}
		sealed class GeometryBatch : IDisposable {
			readonly List<StreamGeometry> geometries = new List<StreamGeometry>();
			StreamGeometry? current;
			StreamGeometryContext? context;
			int count;
			public IReadOnlyList<StreamGeometry> Geometries => geometries;
			public StreamGeometryContext Next() {
				if (context is null || count >= 16) {
					Dispose();
					current = new StreamGeometry();
					geometries.Add(current);
					context = current.Open(); count = 0;
				}
				count++; return context;
			}
			public void Dispose() {
				context?.Close(); context = null;
				if (current is not null && !current.IsFrozen) current.Freeze();
			}
		}
		const double MinimumZoom = 0.08;
		const double MaximumZoom = 12;
		const double HitCellSize = 48;
		readonly Dictionary<MethodDef, Point> positions = new Dictionary<MethodDef, Point>();
		readonly Dictionary<(int X, int Y), List<MethodDef>> hit_cells = new Dictionary<(int, int), List<MethodDef>>();
		readonly Dictionary<MethodDef, List<CallGraphLink>> incident_links = new Dictionary<MethodDef, List<CallGraphLink>>();
		readonly DispatcherTimer geometry_timer;
		readonly GraphVisual graph_visual = new GraphVisual();
		readonly DrawingVisual overlay_visual = new DrawingVisual();
		readonly MatrixTransform graph_transform = new MatrixTransform();
		bool graph_drawing_dirty = true;
		double drawn_zoom;
		int graph_draw_count;
		IThemeService? theme_service;
		CallGraphSlice? slice;
		CallGraphIndex? index;
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
		HighlightedSnippet? preview_snippet;
		bool drag_candidate;
		bool dragging;
		bool geometry_update_pending;
		MouseButton drag_button;

		public event EventHandler<GraphReferenceEventArgs>? RootRequested;
		public event EventHandler<GraphReferenceEventArgs>? NavigateRequested;
		public event EventHandler<GraphReferenceEventArgs>? SelectionChanged;
		public event EventHandler<GraphReferenceEventArgs>? CodePreviewRequested;

		public FunctionMapSurface() {
			AddVisualChild(graph_visual);
			AddVisualChild(overlay_visual);
			graph_visual.RenderTransform = graph_transform;
			Focusable = true;
			ClipToBounds = true;
			Cursor = Cursors.Arrow;
			SnapsToDevicePixels = true;
			geometry_timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) {
				Interval = TimeSpan.FromMilliseconds(40),
			};
			geometry_timer.Tick += GeometryTimer_Tick;
		}

		protected override int VisualChildrenCount => 2;
		protected override Visual GetVisualChild(int index) => index == 0 ? graph_visual : index == 1 ? overlay_visual : throw new ArgumentOutOfRangeException(nameof(index));
		protected override Size MeasureOverride(Size availableSize) { graph_visual.Measure(new Size(1, 1)); return new Size(); }
		protected override Size ArrangeOverride(Size finalSize) { graph_visual.Arrange(new Rect(0, 0, 1, 1)); return finalSize; }

		internal void SetThemeService(IThemeService value) => theme_service = value;

		public void Clear() {
			slice = null;
			index = null;
			root = hovered = selected = null;
			dragged_method = preview_method = null;
			preview_snippet = null;
			geometry_timer.Stop();
			geometry_update_pending = false;
			positions.Clear();
			hit_cells.Clear();
			incident_links.Clear();
			edge_geometry = incoming_edge_geometry = outgoing_edge_geometry = null;
			possible_edge_geometry = null;
			edge_arrow_geometry = incoming_arrow_geometry = outgoing_arrow_geometry = null;
			possible_arrow_geometry = null;
			caller_geometry = callee_geometry = neutral_geometry = null;
			using (graph_visual.RenderOpen()) { }
			using (overlay_visual.RenderOpen()) { }
			InvalidateVisual();
		}

		public void Render(CallGraphSlice value, CallGraphIndex? graph_index = null) {
			slice = value;
			index = graph_index;
			root = value.Nodes.FirstOrDefault(node => node.IsRoot)?.Method;
			selected = root;
			hovered = null;
			HideCodePreview();
			incident_links.Clear();
			foreach (var link in value.Links) {
				if (!incident_links.TryGetValue(link.Source, out var outgoing)) incident_links[link.Source] = outgoing = new List<CallGraphLink>();
				outgoing.Add(link);
				if (ReferenceEquals(link.Source, link.Target)) continue;
				if (!incident_links.TryGetValue(link.Target, out var incoming)) incident_links[link.Target] = incoming = new List<CallGraphLink>();
				incoming.Add(link);
			}
			BuildLayout();
			BuildGeometry();
			ResetView();
		}

		public void ResetView() {
			zoom = 1;
			pan = new Vector();
			InvalidateVisual();
		}

		public void CenterRoot() => ResetView();

		public bool FocusMethod(MethodDef method) {
			if (!positions.TryGetValue(method, out var point))
				return false;
			selected = method;
			hovered = null;
			pan = new Vector(-point.X * zoom, -point.Y * zoom);
			SelectionChanged?.Invoke(this, new GraphReferenceEventArgs(method));
			InvalidateVisual();
			return true;
		}

		void BuildLayout() {
			positions.Clear();
			if (slice is null)
				return;
			const double golden_angle = 2.399963229728653;
			foreach (var group in slice.Nodes.GroupBy(node => node.Layer).OrderBy(group => group.Key)) {
				var nodes = group.OrderBy(node => node.Method.MDToken.Raw).ToArray();
				if (group.Key == 0) {
					foreach (var node in nodes)
						positions[node.Method] = new Point(0, 0);
					continue;
				}
				double anchor_x = Math.Sign(group.Key) * (175 + (Math.Abs(group.Key) - 1) * 245);
				for (int i = 0; i < nodes.Length; i++) {
					double angle = i * golden_angle + Math.Abs(group.Key) * 0.41;
					double radius = 28 * Math.Sqrt(i + 0.5);
					positions[nodes[i].Method] = new Point(anchor_x + Math.Cos(angle) * radius * 0.78,
						Math.Sin(angle) * radius * 1.35);
				}
			}
			RebuildHitIndex();
		}

		void BuildGeometry() {
			if (slice is null)
				return;
			var edges = new GeometryBatch();
			var incoming_edges = new GeometryBatch();
			var outgoing_edges = new GeometryBatch();
			var possible_edges = new GeometryBatch();
			var edge_arrows = new GeometryBatch();
			var incoming_arrows = new GeometryBatch();
			var outgoing_arrows = new GeometryBatch();
			var possible_arrows = new GeometryBatch();
			using (edges) using (incoming_edges) using (outgoing_edges) using (possible_edges)
			using (edge_arrows) using (incoming_arrows) using (outgoing_arrows) using (possible_arrows) {
				foreach (var link in slice.Links) {
					if (dragging && (ReferenceEquals(link.Source, dragged_method) || ReferenceEquals(link.Target, dragged_method))) continue;
					if (!positions.TryGetValue(link.Source, out var from) || !positions.TryGetValue(link.Target, out var to))
						continue;
					bool incoming = link.Target == root;
					bool outgoing = link.Source == root;
					var context = (link.IsPotential ? possible_edges : incoming ? incoming_edges : outgoing ? outgoing_edges : edges).Next();
					var arrow_context = (link.IsPotential ? possible_arrows : incoming ? incoming_arrows : outgoing ? outgoing_arrows : edge_arrows).Next();
					context.BeginFigure(from, false, false);
					context.LineTo(to, true, false);
					AppendArrow(arrow_context, from, to, link.Target == root ? 11 : 7, incoming || outgoing ? 5.4 : 3.8);
				}
			}
			edge_geometry = edges.Geometries;
			incoming_edge_geometry = incoming_edges.Geometries;
			outgoing_edge_geometry = outgoing_edges.Geometries;
			possible_edge_geometry = possible_edges.Geometries;
			edge_arrow_geometry = edge_arrows.Geometries;
			incoming_arrow_geometry = incoming_arrows.Geometries;
			outgoing_arrow_geometry = outgoing_arrows.Geometries;
			possible_arrow_geometry = possible_arrows.Geometries;

			var callers = new GeometryBatch();
			var callees = new GeometryBatch();
			var neutral = new GeometryBatch();
			using (callers) using (callees) using (neutral) {
				foreach (var node in slice.Nodes) {
					if (node.IsRoot || dragging && ReferenceEquals(node.Method, dragged_method) || !positions.TryGetValue(node.Method, out var point))
						continue;
					var context = (node.Layer < 0 ? callers : node.Layer > 0 ? callees : neutral).Next();
					AppendDiamond(context, point, 5.2);
				}
			}
			caller_geometry = callers.Geometries;
			callee_geometry = callees.Geometries;
			neutral_geometry = neutral.Geometries;
			graph_drawing_dirty = true;
		}

		static void AppendArrow(StreamGeometryContext context, Point from, Point to, double target_radius, double size) {
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

		static void AppendDiamond(StreamGeometryContext context, Point point, double radius) {
			context.BeginFigure(new Point(point.X, point.Y - radius), true, true);
			context.LineTo(new Point(point.X + radius, point.Y), true, false);
			context.LineTo(new Point(point.X, point.Y + radius), true, false);
			context.LineTo(new Point(point.X - radius, point.Y), true, false);
		}

		protected override void OnRender(DrawingContext drawing_context) {
			base.OnRender(drawing_context);
			drawing_context.DrawRectangle(ResourceBrush("SystemColorsWindow", Color.FromRgb(30, 30, 30)), null,
				new Rect(0, 0, ActualWidth, ActualHeight));
			if (slice is null)
				return;

			graph_transform.Matrix = new Matrix(zoom, 0, 0, zoom, ActualWidth / 2 + pan.X, ActualHeight / 2 + pan.Y);
			if (graph_drawing_dirty || drawn_zoom <= 0 || zoom / drawn_zoom > 1.3 || zoom / drawn_zoom < 1 / 1.3) {
				using (var graph_context = graph_visual.RenderOpen()) DrawGraph(graph_context);
				graph_drawing_dirty = false;
				drawn_zoom = zoom;
			}
			using (var overlay_context = overlay_visual.RenderOpen()) DrawOverlay(overlay_context);
		}

		void DrawGraph(DrawingContext drawing_context) {
			graph_draw_count++;
			var text_brush = ResourceBrush("SystemColorsWindowText", Colors.Gainsboro);
			var caller_brush = ResourceBrush("SearchMatchHighlight", Color.FromRgb(95, 145, 225));
			var callee_brush = ResourceBrush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var accent = ResourceBrush("HyperlinkNormal", Color.FromRgb(86, 190, 235));
			if (possible_edge_geometry is not null) {
				var possible_pen = new Pen(accent, 1.05 / Math.Sqrt(zoom)) {
					DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
				};
				drawing_context.PushOpacity(0.52);
				DrawBatch(drawing_context, null, possible_pen, possible_edge_geometry);
				DrawBatch(drawing_context, accent, null, possible_arrow_geometry);
				drawing_context.Pop();
			}
			if (edge_geometry is not null) {
				drawing_context.PushOpacity(0.22);
				DrawBatch(drawing_context, null, new Pen(text_brush, 0.75 / Math.Sqrt(zoom)), edge_geometry);
				DrawBatch(drawing_context, text_brush, null, edge_arrow_geometry);
				drawing_context.Pop();
			}
			if (incoming_edge_geometry is not null) {
				drawing_context.PushOpacity(0.72);
				DrawBatch(drawing_context, null, new Pen(caller_brush, 1.45 / Math.Sqrt(zoom)), incoming_edge_geometry);
				DrawBatch(drawing_context, caller_brush, null, incoming_arrow_geometry);
				drawing_context.Pop();
			}
			if (outgoing_edge_geometry is not null) {
				drawing_context.PushOpacity(0.78);
				DrawBatch(drawing_context, null, new Pen(callee_brush, 1.45 / Math.Sqrt(zoom)), outgoing_edge_geometry);
				DrawBatch(drawing_context, callee_brush, null, outgoing_arrow_geometry);
				drawing_context.Pop();
			}
			if (neutral_geometry is not null)
				DrawBatch(drawing_context, ResourceBrush("SystemColorsGrayText", Color.FromRgb(140, 145, 155)), null, neutral_geometry);
			if (caller_geometry is not null)
				DrawBatch(drawing_context, caller_brush, null, caller_geometry);
			if (callee_geometry is not null)
				DrawBatch(drawing_context, callee_brush, null, callee_geometry);
			if (root is not null && !(dragging && ReferenceEquals(root, dragged_method)) && positions.TryGetValue(root, out var root_point))
				drawing_context.DrawEllipse(ResourceBrush("ToolBarButtonChecked", Color.FromRgb(245, 190, 75)),
					new Pen(accent, 1.8 / zoom), root_point, 9, 9);
		}

		void DrawOverlay(DrawingContext drawing_context) {
			var caller_brush = ResourceBrush("SearchMatchHighlight", Color.FromRgb(95, 145, 225));
			var callee_brush = ResourceBrush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var accent = ResourceBrush("HyperlinkNormal", Color.FromRgb(86, 190, 235));
			drawing_context.PushTransform(graph_transform);
			DrawDraggedMethod(drawing_context);
			DrawMarker(drawing_context, selected, ResourceBrush("ToolBarButtonHoverBorder", Colors.White), 8);
			if (hovered != selected)
				DrawMarker(drawing_context, hovered, accent, 7);
			drawing_context.Pop();

			DrawMethodLabel(drawing_context, root, new Vector(12, -25), true);
			if (hovered != root)
				DrawMethodLabel(drawing_context, hovered, new Vector(10, 10), false);
			else if (selected != root)
				DrawMethodLabel(drawing_context, selected, new Vector(10, 10), false);
			DrawLegend(drawing_context, caller_brush, callee_brush);
			DrawMethodInfo(drawing_context, hovered ?? selected ?? root);
			DrawCounter(drawing_context);
			DrawHint(drawing_context);
			DrawCodePreview(drawing_context);
		}

		static void DrawBatch(DrawingContext context, Brush? brush, Pen? pen, IReadOnlyList<StreamGeometry>? geometries) {
			if (geometries is null) return;
			foreach (var geometry in geometries) context.DrawGeometry(brush, pen, geometry);
		}

		void DrawDraggedMethod(DrawingContext context) {
			if (!dragging || dragged_method is null || !positions.TryGetValue(dragged_method, out var point)) return;
			var brush = ResourceBrush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var pen = new Pen(brush, 1.2 / Math.Sqrt(zoom));
			if (incident_links.TryGetValue(dragged_method, out var links)) {
				foreach (var link in links) {
					if (!positions.TryGetValue(link.Source, out var from) || !positions.TryGetValue(link.Target, out var to)) continue;
					context.DrawLine(pen, from, to);
					var arrow = new StreamGeometry();
					using (var arrow_context = arrow.Open()) AppendArrow(arrow_context, from, to, link.Target == root ? 11 : 7, 3.8);
					arrow.Freeze(); context.DrawGeometry(brush, null, arrow);
				}
			}
			context.DrawEllipse(brush, null, point, dragged_method == root ? 9 : 5.2, dragged_method == root ? 9 : 5.2);
		}

		void DrawMarker(DrawingContext drawing_context, MethodDef? method, Brush brush, double radius) {
			if (method is null || method == root || !positions.TryGetValue(method, out var point))
				return;
			drawing_context.DrawEllipse(null, new Pen(brush, 1.8 / zoom), point, radius, radius);
		}

		void DrawMethodLabel(DrawingContext drawing_context, MethodDef? method, Vector offset, bool root_label) {
			if (method is null || !positions.TryGetValue(method, out var graph_point))
				return;
			Point point = GraphToScreen(graph_point) + offset;
			string label = root_label ? CallGraphIndex.Signature(method) : CallGraphIndex.DisplayName(method);
			var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), root_label ? 12 : 11,
				ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), VisualTreeHelper.GetDpi(this).PixelsPerDip) {
				MaxTextWidth = Math.Max(80, Math.Min(520, ActualWidth - Math.Max(0, point.X) - 12)),
				Trimming = TextTrimming.CharacterEllipsis,
			};
			drawing_context.DrawText(text, point);
		}

		void DrawLegend(DrawingContext drawing_context, Brush caller_brush, Brush callee_brush) {
			if (ActualWidth < 280 || ActualHeight < 90)
				return;
			bool has_possible_targets = slice?.Links.Any(link => link.IsPotential) == true;
			double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			var typeface = new Typeface("Segoe UI Semibold");
			var callers = new FormattedText(UiText.MapLegendCallers, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, caller_brush, dpi);
			var root_text = new FormattedText(UiText.MapLegendRoot, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), dpi);
			var callees = new FormattedText(UiText.MapLegendCallees, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, callee_brush, dpi);
			var possible = new FormattedText(UiText.MapLegendPossible, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 9.5, ResourceBrush("SystemColorsGrayText", Colors.Silver), dpi);
			double x = 18;
			double first_arrow_start = x + callers.Width + 10;
			double first_arrow_end = first_arrow_start + 25;
			double root_x = first_arrow_end + 7;
			double second_arrow_start = root_x + root_text.Width + 10;
			double second_arrow_end = second_arrow_start + 25;
			double callees_x = second_arrow_end + 7;
			double panel_width = Math.Max(callees_x + callees.Width + 10, has_possible_targets ? possible.Width + 59 : 0);
			var panel = new Rect(8, 8, panel_width, has_possible_targets ? 51 : 31);
			DrawOverlayPanel(drawing_context, panel);
			drawing_context.DrawText(callers, new Point(x, 16));
			drawing_context.DrawText(root_text, new Point(root_x, 16));
			drawing_context.DrawText(callees, new Point(callees_x, 16));
			DrawLegendArrow(drawing_context, new Point(first_arrow_start, 23), new Point(first_arrow_end, 23), caller_brush);
			DrawLegendArrow(drawing_context, new Point(second_arrow_start, 23), new Point(second_arrow_end, 23), callee_brush);
			if (has_possible_targets) {
				var possible_pen = new Pen(ResourceBrush("HyperlinkNormal", Color.FromRgb(86, 190, 235)), 1.2) {
					DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
				};
				drawing_context.DrawLine(possible_pen, new Point(x, 42), new Point(x + 28, 42));
				drawing_context.DrawText(possible, new Point(x + 36, 34));
			}
		}

		static void DrawLegendArrow(DrawingContext drawing_context, Point from, Point to, Brush brush) {
			drawing_context.DrawLine(new Pen(brush, 1.4), from, to);
			var arrow = new StreamGeometry();
			using (var context = arrow.Open()) {
				context.BeginFigure(to, true, true);
				context.LineTo(new Point(to.X - 5, to.Y - 3), true, false);
				context.LineTo(new Point(to.X - 5, to.Y + 3), true, false);
			}
			drawing_context.DrawGeometry(brush, null, arrow);
		}

		void DrawMethodInfo(DrawingContext drawing_context, MethodDef? method) {
			if (method is null || ActualWidth < 530 || ActualHeight < 180)
				return;
			double width = Math.Min(390, ActualWidth * 0.42);
			var panel = new Rect(ActualWidth - width - 9, 8, width, 130);
			DrawOverlayPanel(drawing_context, panel);
			double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			var heading = new FormattedText(CallGraphIndex.DisplayName(method), CultureInfo.CurrentUICulture,
				FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 12,
				ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), dpi) {
				MaxTextWidth = width - 20,
				Trimming = TextTrimming.CharacterEllipsis,
			};
			drawing_context.DrawText(heading, new Point(panel.Left + 10, panel.Top + 8));
			int callers = index?.Incoming(method).Count ?? 0;
			int callees = index?.Outgoing(method).Count ?? 0;
			int call_sites = index?.Outgoing(method).Sum(link => link.CallSiteCount) ?? 0;
			int instructions = method.HasBody ? method.Body.Instructions.Count : 0;
			string detail = (method.DeclaringType?.FullName ?? "<Module>") + Environment.NewLine +
				$"token 0x{method.MDToken.Raw:X8}   •   " + UiText.MethodInfo(callers, callees, call_sites, instructions, method.IsStatic) + "\n" + AnalysisDetails.Summary(slice, method);
			var details = new FormattedText(detail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), 10.5, ResourceBrush("SystemColorsGrayText", Colors.Silver), dpi) {
				MaxTextWidth = width - 20,
				MaxTextHeight = 94,
				Trimming = TextTrimming.CharacterEllipsis,
			};
			drawing_context.DrawText(details, new Point(panel.Left + 10, panel.Top + 31));
		}

		void DrawHint(DrawingContext drawing_context) {
			if (ActualWidth < 570)
				return;
			string value = UiText.MapHint;
			var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), 10.5, ResourceBrush("SystemColorsGrayText", Colors.Gray),
				VisualTreeHelper.GetDpi(this).PixelsPerDip);
			drawing_context.DrawText(text, new Point(Math.Max(9, ActualWidth - text.Width - 10),
				Math.Max(4, ActualHeight - text.Height - 7)));
		}

		void DrawCodePreview(DrawingContext drawing_context) {
			if (preview_method is null || hovered != preview_method || dragging || ActualWidth < 260 || ActualHeight < 160)
				return;
			double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			double maximum_width = Math.Min(580, Math.Max(240, ActualWidth - 30));
			double maximum_height = Math.Min(360, Math.Max(100, ActualHeight - 30));
			var heading = new FormattedText(CallGraphIndex.Signature(preview_method), CultureInfo.CurrentUICulture,
				FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11.5,
				ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), dpi) {
				MaxTextWidth = maximum_width - 20,
				Trimming = TextTrimming.CharacterEllipsis,
			};
			string source = preview_snippet?.Text ?? UiText.LoadingPreview;
			var code = new FormattedText(source, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Consolas"), 10.5, ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), dpi) {
				MaxTextWidth = maximum_width - 20,
				MaxTextHeight = maximum_height - 43,
				Trimming = TextTrimming.CharacterEllipsis,
			};
			ApplyPreviewColors(code, preview_snippet);
			double width = Math.Min(maximum_width, Math.Max(280, Math.Max(heading.Width, code.Width) + 20));
			double height = Math.Min(maximum_height, 33 + code.Height + 10);
			double x = preview_position.X + 16;
			double y = preview_position.Y + 16;
			if (x + width > ActualWidth - 8)
				x = preview_position.X - width - 16;
			if (y + height > ActualHeight - 8)
				y = preview_position.Y - height - 16;
			x = Math.Max(8, Math.Min(x, ActualWidth - width - 8));
			y = Math.Max(8, Math.Min(y, ActualHeight - height - 8));
			var panel = new Rect(x, y, width, height);
			DrawOverlayPanel(drawing_context, panel);
			drawing_context.DrawText(heading, new Point(x + 10, y + 7));
			drawing_context.DrawLine(new Pen(ResourceBrush("CommonControlsTextBoxBorder", Color.FromRgb(75, 75, 80)), 1),
				new Point(x + 1, y + 29), new Point(x + width - 1, y + 29));
			drawing_context.DrawText(code, new Point(x + 10, y + 34));
		}

		void ApplyPreviewColors(FormattedText text, HighlightedSnippet? snippet) {
			if (snippet is null || theme_service is null)
				return;
			foreach (var span in snippet.Spans) {
				if (span.Start < 0 || span.Start >= snippet.Text.Length || span.Length <= 0)
					continue;
				int length = Math.Min(span.Length, snippet.Text.Length - span.Start);
				uint color_value = (uint)ColorType.Text + (uint)span.Color;
				var color = theme_service.Theme.GetTextColor((ColorType)color_value);
				if (color.Foreground is not null)
					text.SetForegroundBrush(color.Foreground, span.Start, length);
				if (color.FontWeight is not null)
					text.SetFontWeight(color.FontWeight.Value, span.Start, length);
				if (color.FontStyle is not null)
					text.SetFontStyle(color.FontStyle.Value, span.Start, length);
			}
		}

		void ShowCodePreview() {
			if (hovered is null || drag_candidate || dragging)
				return;
			preview_method = hovered;
			preview_snippet = null;
			preview_position = pointer_position;
			InvalidateVisual();
			CodePreviewRequested?.Invoke(this, new GraphReferenceEventArgs(hovered));
		}

		void HideCodePreview() {
			preview_method = null;
			preview_snippet = null;
		}

		public void SetCodePreview(MethodDef method, HighlightedSnippet snippet) {
			if (!ReferenceEquals(method, preview_method) || !ReferenceEquals(method, hovered))
				return;
			preview_snippet = snippet;
			InvalidateVisual();
		}

		void DrawOverlayPanel(DrawingContext drawing_context, Rect rect) {
			drawing_context.PushOpacity(0.92);
			drawing_context.DrawRoundedRectangle(ResourceBrush("EnvironmentBackground", Color.FromRgb(40, 40, 43)),
				new Pen(ResourceBrush("CommonControlsTextBoxBorder", Color.FromRgb(75, 75, 80)), 1), rect, 2, 2);
			drawing_context.Pop();
		}

		void DrawCounter(DrawingContext drawing_context) {
			if (slice is null)
				return;
			string text_value = UiText.MethodsLinks(slice.Nodes.Count, slice.Links.Count, slice.IsTruncated);
			var text = new FormattedText(text_value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), 11, ResourceBrush("SystemColorsGrayText", Colors.Gray),
				VisualTreeHelper.GetDpi(this).PixelsPerDip);
			drawing_context.DrawText(text, new Point(9, Math.Max(4, ActualHeight - text.Height - 7)));
		}

		Brush ResourceBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

		Point GraphToScreen(Point point) => new Point(ActualWidth / 2 + pan.X + point.X * zoom,
			ActualHeight / 2 + pan.Y + point.Y * zoom);

		Point ScreenToGraph(Point point) => new Point((point.X - ActualWidth / 2 - pan.X) / zoom,
			(point.Y - ActualHeight / 2 - pan.Y) / zoom);

		static (int X, int Y) HitCell(Point point) =>
			((int)Math.Floor(point.X / HitCellSize), (int)Math.Floor(point.Y / HitCellSize));

		void RebuildHitIndex() {
			hit_cells.Clear();
			foreach (var pair in positions)
				AddToHitIndex(pair.Key, pair.Value);
		}

		void AddToHitIndex(MethodDef method, Point point) {
			var cell = HitCell(point);
			if (!hit_cells.TryGetValue(cell, out var methods))
				hit_cells.Add(cell, methods = new List<MethodDef>());
			methods.Add(method);
		}

		void MoveInHitIndex(MethodDef method, Point old_point, Point new_point) {
			var old_cell = HitCell(old_point);
			var new_cell = HitCell(new_point);
			if (old_cell == new_cell)
				return;
			if (hit_cells.TryGetValue(old_cell, out var old_methods)) {
				old_methods.Remove(method);
				if (old_methods.Count == 0)
					hit_cells.Remove(old_cell);
			}
			AddToHitIndex(method, new_point);
		}

		MethodDef? HitTestMethod(Point screen_point) {
			Point graph_point = ScreenToGraph(screen_point);
			double maximum_distance = Math.Max(8, 12 / zoom);
			double maximum_distance_squared = maximum_distance * maximum_distance;
			MethodDef? result = null;
			double best = maximum_distance_squared;
			var center_cell = HitCell(graph_point);
			int cell_radius = Math.Max(1, (int)Math.Ceiling(maximum_distance / HitCellSize));
			for (int x = center_cell.X - cell_radius; x <= center_cell.X + cell_radius; x++) {
				for (int y = center_cell.Y - cell_radius; y <= center_cell.Y + cell_radius; y++) {
					if (!hit_cells.TryGetValue((x, y), out var methods))
						continue;
					foreach (var method in methods) {
						Point position = positions[method];
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

		void ScheduleGeometryUpdate() {
			geometry_update_pending = true;
			if (!geometry_timer.IsEnabled)
				geometry_timer.Start();
		}

		void GeometryTimer_Tick(object? sender, EventArgs e) {
			if (!geometry_update_pending) {
				geometry_timer.Stop();
				return;
			}
			geometry_update_pending = false;
			if (slice is null)
				return;
			InvalidateVisual();
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e) {
			if (e.Delta == 0)
				return;
			Point pointer = e.GetPosition(this);
			Point anchor = ScreenToGraph(pointer);
			double next = Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom * Math.Pow(1.18, e.Delta / 120.0)));
			zoom = next;
			pan = new Vector(pointer.X - ActualWidth / 2 - anchor.X * zoom,
				pointer.Y - ActualHeight / 2 - anchor.Y * zoom);
			InvalidateVisual();
			e.Handled = true;
		}

		protected override void OnMouseDown(MouseButtonEventArgs e) {
			Focus();
			Point point = e.GetPosition(this);
			pointer_position = point;
			var method = HitTestMethod(point);
			if (e.ChangedButton == MouseButton.Right) {
				if (method is not null)
					OpenContextMenu(method);
				e.Handled = true;
				return;
			}
			if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle)
				return;
			if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && method is not null) {
				RootRequested?.Invoke(this, new GraphReferenceEventArgs(method));
				e.Handled = true;
				return;
			}
			drag_button = e.ChangedButton;
			drag_candidate = true;
			dragging = false;
			drag_start = point;
			pan_start = pan;
			dragged_method = e.ChangedButton == MouseButton.Left ? method : null;
			if (dragged_method is not null)
				dragged_method_start = positions[dragged_method];
			HideCodePreview();
			CaptureMouse();
			e.Handled = true;
		}

		protected override void OnMouseMove(MouseEventArgs e) {
			Point point = e.GetPosition(this);
			pointer_position = point;
			if (drag_candidate) {
				bool pressed = drag_button == MouseButton.Left ? e.LeftButton == MouseButtonState.Pressed : e.MiddleButton == MouseButtonState.Pressed;
				if (!pressed) {
					EndDrag(point);
					return;
				}
				Vector delta = point - drag_start;
				if (!dragging && (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
					Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)) {
					dragging = true;
					Cursor = dragged_method is null ? Cursors.ScrollAll : Cursors.SizeAll;
					if (dragged_method is not null) BuildGeometry();
				}
				if (dragging) {
					if (dragged_method is not null) {
						Point old_position = positions[dragged_method];
						Point new_position = dragged_method_start + delta / zoom;
						positions[dragged_method] = new_position;
						MoveInHitIndex(dragged_method, old_position, new_position);
						ScheduleGeometryUpdate();
					}
					else {
						pan = pan_start + delta;
						InvalidateVisual();
					}
				}
				return;
			}

			var next = HitTestMethod(point);
			if (next == hovered)
				return;
			hovered = next;
			ToolTip = null;
			Cursor = hovered is null ? Cursors.Arrow : Cursors.Hand;
			if (hovered is null) {
				HideCodePreview();
				InvalidateVisual();
			}
			else
				ShowCodePreview();
		}

		protected override void OnMouseUp(MouseButtonEventArgs e) {
			if (drag_candidate && e.ChangedButton == drag_button) {
				EndDrag(e.GetPosition(this));
				e.Handled = true;
			}
		}

		protected override void OnLostMouseCapture(MouseEventArgs e) {
			base.OnLostMouseCapture(e);
			bool rebuild = dragging && dragged_method is not null;
			drag_candidate = dragging = false;
			dragged_method = null;
			geometry_timer.Stop(); geometry_update_pending = false;
			if (rebuild && slice is not null) { BuildGeometry(); InvalidateVisual(); }
			Cursor = Cursors.Arrow;
		}

		protected override void OnMouseLeave(MouseEventArgs e) {
			base.OnMouseLeave(e);
			if (drag_candidate)
				return;
			hovered = null;
			HideCodePreview();
			Cursor = Cursors.Arrow;
			InvalidateVisual();
		}

		void EndDrag(Point point) {
			bool was_dragging = dragging;
			var moved_method = dragged_method;
			drag_candidate = dragging = false;
			dragged_method = null;
			geometry_timer.Stop();
			if ((geometry_update_pending || was_dragging && moved_method is not null) && slice is not null) {
				geometry_update_pending = false;
				BuildGeometry();
			}
			ReleaseMouseCapture();
			Cursor = Cursors.Arrow;
			if (drag_button == MouseButton.Left) {
				var method = was_dragging ? moved_method : HitTestMethod(point);
				if (method is not null) {
					selected = method;
					SelectionChanged?.Invoke(this, new GraphReferenceEventArgs(method));
				}
			}
			hovered = HitTestMethod(point);
			Cursor = hovered is null ? Cursors.Arrow : Cursors.Hand;
			if (hovered is null) {
				HideCodePreview();
				InvalidateVisual();
			}
			else
				ShowCodePreview();
		}

		void OpenContextMenu(MethodDef method) {
			var menu = new ContextMenu();
			var explore = new MenuItem { Header = UiText.ExploreHere, FontWeight = FontWeights.SemiBold };
			explore.Click += (_, __) => RootRequested?.Invoke(this, new GraphReferenceEventArgs(method));
			menu.Items.Add(explore);
			var open = new MenuItem { Header = UiText.OpenSource };
			open.Click += (_, __) => NavigateRequested?.Invoke(this, new GraphReferenceEventArgs(method));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = UiText.CopyFullName };
			copy.Click += (_, __) => Clipboard.SetText(CallGraphIndex.DisplayName(method));
			menu.Items.Add(copy);
			menu.PlacementTarget = this;
			menu.IsOpen = true;
		}
	}
}
