using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
		sealed class FunctionMapSurface : FrameworkElement {
		const double MinimumZoom = 0.08;
		const double MaximumZoom = 12;
		readonly Dictionary<MethodDef, Point> positions = new Dictionary<MethodDef, Point>();
		CallGraphSlice? slice;
		CallGraphIndex? index;
		MethodDef? root;
		MethodDef? hovered;
		MethodDef? selected;
		StreamGeometry? edge_geometry;
		StreamGeometry? incoming_edge_geometry;
		StreamGeometry? outgoing_edge_geometry;
		StreamGeometry? edge_arrow_geometry;
		StreamGeometry? incoming_arrow_geometry;
		StreamGeometry? outgoing_arrow_geometry;
		StreamGeometry? caller_geometry;
		StreamGeometry? callee_geometry;
		StreamGeometry? neutral_geometry;
		double zoom = 1;
		Vector pan;
		Point drag_start;
		Vector pan_start;
		bool drag_candidate;
		bool dragging;
		MouseButton drag_button;

		public event EventHandler<GraphReferenceEventArgs>? RootRequested;
		public event EventHandler<GraphReferenceEventArgs>? NavigateRequested;
		public event EventHandler<GraphReferenceEventArgs>? SelectionChanged;

		public FunctionMapSurface() {
			Focusable = true;
			ClipToBounds = true;
			Cursor = Cursors.Arrow;
			SnapsToDevicePixels = true;
		}

		public void Clear() {
			slice = null;
			index = null;
			root = hovered = selected = null;
			positions.Clear();
			edge_geometry = incoming_edge_geometry = outgoing_edge_geometry = null;
			edge_arrow_geometry = incoming_arrow_geometry = outgoing_arrow_geometry = null;
			caller_geometry = callee_geometry = neutral_geometry = null;
			InvalidateVisual();
		}

		public void Render(CallGraphSlice value, CallGraphIndex? graph_index = null) {
			slice = value;
			index = graph_index;
			root = value.Nodes.FirstOrDefault(node => node.IsRoot)?.Method;
			selected = root;
			hovered = null;
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
		}

		void BuildGeometry() {
			if (slice is null)
				return;
			var edges = new StreamGeometry();
			var incoming_edges = new StreamGeometry();
			var outgoing_edges = new StreamGeometry();
			var edge_arrows = new StreamGeometry();
			var incoming_arrows = new StreamGeometry();
			var outgoing_arrows = new StreamGeometry();
			using (var edge_context = edges.Open())
			using (var incoming_context = incoming_edges.Open())
			using (var outgoing_context = outgoing_edges.Open())
			using (var edge_arrow_context = edge_arrows.Open())
			using (var incoming_arrow_context = incoming_arrows.Open())
			using (var outgoing_arrow_context = outgoing_arrows.Open()) {
				foreach (var link in slice.Links) {
					if (!positions.TryGetValue(link.Source, out var from) || !positions.TryGetValue(link.Target, out var to))
						continue;
					bool incoming = link.Target == root;
					bool outgoing = link.Source == root;
					var context = incoming ? incoming_context : outgoing ? outgoing_context : edge_context;
					var arrow_context = incoming ? incoming_arrow_context : outgoing ? outgoing_arrow_context : edge_arrow_context;
					context.BeginFigure(from, false, false);
					context.LineTo(to, true, false);
					AppendArrow(arrow_context, from, to, link.Target == root ? 11 : 7, incoming || outgoing ? 5.4 : 3.8);
				}
			}
			edges.Freeze();
			incoming_edges.Freeze();
			outgoing_edges.Freeze();
			edge_arrows.Freeze();
			incoming_arrows.Freeze();
			outgoing_arrows.Freeze();
			edge_geometry = edges;
			incoming_edge_geometry = incoming_edges;
			outgoing_edge_geometry = outgoing_edges;
			edge_arrow_geometry = edge_arrows;
			incoming_arrow_geometry = incoming_arrows;
			outgoing_arrow_geometry = outgoing_arrows;

			var callers = new StreamGeometry();
			var callees = new StreamGeometry();
			var neutral = new StreamGeometry();
			using (var caller_context = callers.Open())
			using (var callee_context = callees.Open())
			using (var neutral_context = neutral.Open()) {
				foreach (var node in slice.Nodes) {
					if (node.IsRoot || !positions.TryGetValue(node.Method, out var point))
						continue;
					var context = node.Layer < 0 ? caller_context : node.Layer > 0 ? callee_context : neutral_context;
					AppendDiamond(context, point, 5.2);
				}
			}
			callers.Freeze();
			callees.Freeze();
			neutral.Freeze();
			caller_geometry = callers;
			callee_geometry = callees;
			neutral_geometry = neutral;
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

			var transform = new MatrixTransform(zoom, 0, 0, zoom,
				ActualWidth / 2 + pan.X, ActualHeight / 2 + pan.Y);
			drawing_context.PushTransform(transform);
			var text_brush = ResourceBrush("SystemColorsWindowText", Colors.Gainsboro);
			var caller_brush = ResourceBrush("SearchMatchHighlight", Color.FromRgb(95, 145, 225));
			var callee_brush = ResourceBrush("HyperlinkNormal", Color.FromRgb(75, 190, 170));
			var accent = ResourceBrush("HyperlinkNormal", Color.FromRgb(86, 190, 235));
			if (edge_geometry is not null) {
				drawing_context.PushOpacity(0.22);
				drawing_context.DrawGeometry(null, new Pen(text_brush, 0.75 / Math.Sqrt(zoom)), edge_geometry);
				drawing_context.DrawGeometry(text_brush, null, edge_arrow_geometry);
				drawing_context.Pop();
			}
			if (incoming_edge_geometry is not null) {
				drawing_context.PushOpacity(0.72);
				drawing_context.DrawGeometry(null, new Pen(caller_brush, 1.45 / Math.Sqrt(zoom)), incoming_edge_geometry);
				drawing_context.DrawGeometry(caller_brush, null, incoming_arrow_geometry);
				drawing_context.Pop();
			}
			if (outgoing_edge_geometry is not null) {
				drawing_context.PushOpacity(0.78);
				drawing_context.DrawGeometry(null, new Pen(callee_brush, 1.45 / Math.Sqrt(zoom)), outgoing_edge_geometry);
				drawing_context.DrawGeometry(callee_brush, null, outgoing_arrow_geometry);
				drawing_context.Pop();
			}
			if (neutral_geometry is not null)
				drawing_context.DrawGeometry(ResourceBrush("SystemColorsGrayText", Color.FromRgb(140, 145, 155)), null, neutral_geometry);
			if (caller_geometry is not null)
				drawing_context.DrawGeometry(caller_brush, null, caller_geometry);
			if (callee_geometry is not null)
				drawing_context.DrawGeometry(callee_brush, null, callee_geometry);
			if (root is not null && positions.TryGetValue(root, out var root_point))
				drawing_context.DrawEllipse(ResourceBrush("ToolBarButtonChecked", Color.FromRgb(245, 190, 75)),
					new Pen(accent, 1.8 / zoom), root_point, 9, 9);
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
			double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			var typeface = new Typeface("Segoe UI Semibold");
			var callers = new FormattedText(UiText.MapLegendCallers, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, caller_brush, dpi);
			var root_text = new FormattedText(UiText.MapLegendRoot, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, ResourceBrush("SystemColorsWindowText", Colors.Gainsboro), dpi);
			var callees = new FormattedText(UiText.MapLegendCallees, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				typeface, 10.5, callee_brush, dpi);
			double x = 18;
			double first_arrow_start = x + callers.Width + 10;
			double first_arrow_end = first_arrow_start + 25;
			double root_x = first_arrow_end + 7;
			double second_arrow_start = root_x + root_text.Width + 10;
			double second_arrow_end = second_arrow_start + 25;
			double callees_x = second_arrow_end + 7;
			var panel = new Rect(8, 8, callees_x + callees.Width + 10, 31);
			DrawOverlayPanel(drawing_context, panel);
			drawing_context.DrawText(callers, new Point(x, 16));
			drawing_context.DrawText(root_text, new Point(root_x, 16));
			drawing_context.DrawText(callees, new Point(callees_x, 16));
			DrawLegendArrow(drawing_context, new Point(first_arrow_start, 23), new Point(first_arrow_end, 23), caller_brush);
			DrawLegendArrow(drawing_context, new Point(second_arrow_start, 23), new Point(second_arrow_end, 23), callee_brush);
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
			var panel = new Rect(ActualWidth - width - 9, 8, width, 112);
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
				$"token 0x{method.MDToken.Raw:X8}   •   " + UiText.MethodInfo(callers, callees, call_sites, instructions, method.IsStatic);
			var details = new FormattedText(detail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), 10.5, ResourceBrush("SystemColorsGrayText", Colors.Silver), dpi) {
				MaxTextWidth = width - 20,
				MaxTextHeight = 76,
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

		MethodDef? HitTestMethod(Point screen_point) {
			Point graph_point = ScreenToGraph(screen_point);
			double maximum_distance_squared = Math.Pow(Math.Max(8, 12 / zoom), 2);
			MethodDef? result = null;
			double best = maximum_distance_squared;
			foreach (var pair in positions) {
				double dx = pair.Value.X - graph_point.X;
				double dy = pair.Value.Y - graph_point.Y;
				double distance = dx * dx + dy * dy;
				if (distance >= best)
					continue;
				best = distance;
				result = pair.Key;
			}
			return result;
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
			CaptureMouse();
			e.Handled = true;
		}

		protected override void OnMouseMove(MouseEventArgs e) {
			Point point = e.GetPosition(this);
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
					Cursor = Cursors.ScrollAll;
				}
				if (dragging) {
					pan = pan_start + delta;
					InvalidateVisual();
				}
				return;
			}

			var next = HitTestMethod(point);
			if (next == hovered)
				return;
			hovered = next;
			ToolTip = hovered is null ? null : CallGraphIndex.Signature(hovered);
			Cursor = hovered is null ? Cursors.Arrow : Cursors.Hand;
			InvalidateVisual();
		}

		protected override void OnMouseUp(MouseButtonEventArgs e) {
			if (drag_candidate && e.ChangedButton == drag_button) {
				EndDrag(e.GetPosition(this));
				e.Handled = true;
			}
		}

		protected override void OnLostMouseCapture(MouseEventArgs e) {
			base.OnLostMouseCapture(e);
			drag_candidate = dragging = false;
			Cursor = Cursors.Arrow;
		}

		void EndDrag(Point point) {
			bool was_dragging = dragging;
			drag_candidate = dragging = false;
			ReleaseMouseCapture();
			Cursor = Cursors.Arrow;
			if (!was_dragging && drag_button == MouseButton.Left) {
				var method = HitTestMethod(point);
				if (method is not null) {
					selected = method;
					SelectionChanged?.Invoke(this, new GraphReferenceEventArgs(method));
					InvalidateVisual();
				}
			}
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
