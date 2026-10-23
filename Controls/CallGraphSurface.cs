using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph.Controls {
	sealed class CallGraphSurface : UserControl {
		sealed class EdgeLayer : FrameworkElement {
			readonly CallGraphSurface owner;
			DrawingGroup? drawing;
			public EdgeLayer(CallGraphSurface owner) {
				this.owner = owner; ToolTip = " ";
				ToolTipOpening += (_, e) => {
					Point point = Mouse.GetPosition(this);
					var pen = new Pen(Brushes.Black, 7 / owner.Zoom);
					var hit = owner.edge_visuals.FirstOrDefault(edge => edge.Curve.Data.StrokeContains(pen, point));
					if (hit is null) e.Handled = true; else ToolTip = hit.Link.ToolTip;
				};
			}
			public void InvalidateDrawing() { drawing = null; InvalidateVisual(); }
			protected override void OnRender(DrawingContext context) {
				if (drawing is null) { drawing = new DrawingGroup(); using (var target = drawing.Open()) owner.DrawEdges(target); }
				context.DrawDrawing(drawing);
			}
		}
		sealed class EdgeVisual {
			public GraphLinkPresentation Link { get; }
			public Path Curve { get; }
			public Path Arrow { get; }
			public FrameworkElement? Label { get; }
			public Vector LabelOffset { get; }

			public EdgeVisual(GraphLinkPresentation link, Path curve, Path arrow,
				FrameworkElement? label, Vector label_offset) {
				Link = link;
				Curve = curve;
				Arrow = arrow;
				Label = label;
				LabelOffset = label_offset;
			}
		}

		readonly struct EdgePlacement {
			public Point From { get; }
			public Point To { get; }
			public Point Control1 { get; }
			public Point Control2 { get; }
			public Point LabelPoint { get; }

			public EdgePlacement(Point from, Point to, Point control1, Point control2, Point label_point) {
				From = from;
				To = to;
				Control1 = control1;
				Control2 = control2;
				LabelPoint = label_point;
			}
		}

		const double ColumnGap = 115;
		const double NodeGap = 30;
		const double CanvasMargin = 60;
		const double MinimumHorizontalPanSpace = 420;
		const double MinimumVerticalPanSpace = 260;
		const double DefaultNodeWidth = 430;
		const double HeaderHeight = 47;
		const double MinimumNodeHeight = 78;

		readonly ScrollViewer scroll_viewer;
		readonly Border pan_host;
		readonly Canvas canvas;
		readonly Canvas extent_host;
		readonly ScaleTransform scale_transform;
		readonly EdgeLayer edge_layer;
		readonly DispatcherTimer visibility_timer;
		bool visibility_update_pending;
		IThemeService? theme_service;
		readonly Dictionary<object, Border> node_borders = new Dictionary<object, Border>();
		readonly Dictionary<object, Border> cached_borders = new Dictionary<object, Border>();
		readonly Dictionary<object, Rect> node_rects = new Dictionary<object, Rect>();
		readonly List<EdgeVisual> edge_visuals = new List<EdgeVisual>();
		readonly Dictionary<object, GraphNodePresentation> node_presentations = new Dictionary<object, GraphNodePresentation>();
		readonly Dictionary<object, HighlightedSnippet> node_snippets = new Dictionary<object, HighlightedSnippet>();
		bool updating_visibility;
		double node_width = DefaultNodeWidth;
		bool wrap_long_lines = true;
		bool syntax_highlighting = true;
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

		public event EventHandler<GraphReferenceEventArgs>? NavigateRequested;
		public event EventHandler<GraphReferenceEventArgs>? RootRequested;
		public event EventHandler<GraphReferenceEventArgs>? SelectionChanged;
		public event EventHandler? VisibleMethodsChanged;

		public double Zoom {
			get => scale_transform.ScaleX;
			set {
				double next = Math.Max(0.01, Math.Min(4.0, value));
				if (Math.Abs(next - scale_transform.ScaleX) < 0.001)
					return;
				scale_transform.ScaleX = next;
				scale_transform.ScaleY = next;
				UpdateExtent();
			}
		}

		public CallGraphSurface() : this(null) { }

		internal CallGraphSurface(IThemeService? theme_service) {
			this.theme_service = theme_service;
			Focusable = true;
			canvas = new Canvas { MinWidth = 1, MinHeight = 1, ClipToBounds = false };
			edge_layer = new EdgeLayer(this);
			visibility_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(35) };
			visibility_timer.Tick += (_, __) => { visibility_timer.Stop(); UpdateVisibleNodes(); };
			canvas.SetResourceReference(Panel.BackgroundProperty, "SystemColorsWindow");
			
			
			TextOptions.SetTextRenderingMode(canvas, TextRenderingMode.Grayscale);
			TextOptions.SetTextFormattingMode(canvas, TextFormattingMode.Ideal);
			RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.HighQuality);
			scale_transform = new ScaleTransform(1, 1);
			canvas.RenderTransform = scale_transform;
			extent_host = new Canvas { ClipToBounds = false };
			extent_host.Children.Add(canvas);
			pan_host = new Border {
				Child = extent_host,
				Padding = new Thickness(MinimumHorizontalPanSpace, MinimumVerticalPanSpace,
					MinimumHorizontalPanSpace, MinimumVerticalPanSpace),
			};
			pan_host.SetResourceReference(Border.BackgroundProperty, "SystemColorsWindow");
			scroll_viewer = new ScrollViewer {
				Content = pan_host,
				
				
				HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
				VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
				CanContentScroll = false,
				PanningMode = PanningMode.Both,
			};
			scroll_viewer.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow");
			scroll_viewer.SizeChanged += (_, __) => UpdatePanSpace();
			scroll_viewer.ScrollChanged += (_, __) => { visibility_timer.Stop(); visibility_timer.Start(); };
			Content = scroll_viewer;

			PreviewMouseWheel += OnPreviewMouseWheel;
			PreviewMouseDown += OnPreviewMouseDown;
			PreviewMouseMove += OnPreviewMouseMove;
			PreviewMouseUp += OnPreviewMouseUp;
			LostMouseCapture += (_, __) => EndPan();
		}

		internal void Configure(CallGraphSettings settings) {
			node_width = settings.BlockWidth;
			wrap_long_lines = settings.WrapLongLines;
			syntax_highlighting = settings.SyntaxHighlighting;
		}

		internal void SetThemeService(IThemeService value) => theme_service = value;
		void UpdateExtent() {
			extent_host.Width = Math.Max(1, canvas.Width * Zoom);
			extent_host.Height = Math.Max(1, canvas.Height * Zoom);
			edge_layer.Width = Math.Max(1, canvas.Width);
			edge_layer.Height = Math.Max(1, canvas.Height);
		}

		void UpdatePanSpace() {
			double width = scroll_viewer.ViewportWidth > 0 ? scroll_viewer.ViewportWidth : scroll_viewer.ActualWidth;
			double height = scroll_viewer.ViewportHeight > 0 ? scroll_viewer.ViewportHeight : scroll_viewer.ActualHeight;
			pan_host.Padding = new Thickness(
				Math.Max(MinimumHorizontalPanSpace, width * 0.6),
				Math.Max(MinimumVerticalPanSpace, height * 0.55),
				Math.Max(MinimumHorizontalPanSpace, width * 0.6),
				Math.Max(MinimumVerticalPanSpace, height * 0.55));
		}

		public void Clear(string? message = null) {
			visibility_timer.Stop();
			edge_layer.InvalidateDrawing();
			canvas.Children.Clear();
			node_borders.Clear();
			cached_borders.Clear();
			node_presentations.Clear();
			node_snippets.Clear();
			node_rects.Clear();
			edge_visuals.Clear();
			root_reference = selected_reference = null;
			canvas.Width = Math.Max(1, ActualWidth);
			canvas.Height = Math.Max(1, ActualHeight);
			UpdateExtent();
			if (!string.IsNullOrWhiteSpace(message)) {
				var text = new TextBlock {
					Text = message,
					Margin = new Thickness(18),
					FontSize = 13,
				};
				text.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
				canvas.Children.Add(text);
				Canvas.SetLeft(text, 20);
				Canvas.SetTop(text, 20);
			}
		}

		public void Render(CallGraphSlice slice, IReadOnlyDictionary<MethodDef, HighlightedSnippet> snippets) {
			var nodes = slice.Nodes.Select(node => new GraphNodePresentation(
				node.Method, node.Layer, node.IsRoot, node.IsExternal,
				node.Method.Name,
				(node.Method.DeclaringType?.FullName ?? "<Module>") + (AnalysisDetails.Summary(slice, node.Method).Length == 0 ? string.Empty : " • " + AnalysisDetails.Summary(slice, node.Method)),
				CallGraphIndex.Signature(node.Method) + $"   token 0x{node.Method.MDToken.Raw:X8}" + AnalysisDetails.ToolTip(slice, node.Method),
				CallGraphIndex.DisplayName(node.Method))).ToArray();
			var links = slice.Links.Select(link => new GraphLinkPresentation(
				link.Source, link.Target, link.CallSiteCount, BuildLinkToolTip(link),
				link.OpCodes.Contains("calli") ? "calli" : link.OpCodes.Contains("delegate invoke") ? "delegate" : link.OpCodes.Contains("reflection invoke") ? "reflection" : link.OpCodes.Contains("state machine") ? "async" : link.OpCodes.Contains("scheduled callback") ? "callback" : link.OpCodes.Contains("dynamic invoke") ? "dynamic" : string.Empty,
				link.IsPotential ? GraphLinkKind.PossibleCall : GraphLinkKind.Call)).ToArray();
			RenderCore(new GraphPresentation(nodes, links),
				snippets.ToDictionary(pair => (object)pair.Key, pair => pair.Value),
				UiText.Pick("No calls were found for the selected method.", "Для выбранного метода вызовы не найдены."), false);
		}

		public void Render(ControlFlowGraph graph, int maximum_lines = 0) {
			var nodes = graph.Blocks.Select(block => new GraphNodePresentation(
				block, block.Layer, block.IsEntry, false,
				block.Title,
				block.IsEntry ? UiText.Pick("entry", "вход") + " • " + block.Description : block.Description,
				$"{CallGraphIndex.Signature(block.Method)}\n{block.Title}–IL_{block.EndOffset:X4}",
				block.Snippet.Text)).ToArray();
			var links = graph.Edges.Select(edge => new GraphLinkPresentation(
				edge.Source, edge.Target, 1,
				$"{edge.Source.Title} → {edge.Target.Title}\n{edge.Kind}" +
					(edge.Label.Length == 0 ? string.Empty : ": " + edge.Label),
				edge.Label, edge.Kind switch {
					ControlFlowEdgeKind.FallThrough => GraphLinkKind.FallThrough,
					ControlFlowEdgeKind.Conditional => GraphLinkKind.Conditional,
					ControlFlowEdgeKind.Switch => GraphLinkKind.Switch,
					ControlFlowEdgeKind.Exception => GraphLinkKind.Exception,
					_ => GraphLinkKind.Call,
				})).ToArray();
			RenderCore(new GraphPresentation(nodes, links),
				graph.Blocks.ToDictionary(block => (object)block, block => block.Snippet.LimitLines(maximum_lines)),
				UiText.Pick("The selected method does not have a managed IL body.", "У выбранного метода нет управляемого IL-тела."), true);
		}

		void RenderCore(GraphPresentation graph, IReadOnlyDictionary<object, HighlightedSnippet> snippets,
			string empty_message, bool use_vertical_layout) {
			canvas.Children.Clear();
			node_borders.Clear();
			cached_borders.Clear();
			node_rects.Clear();
			edge_visuals.Clear();
			node_presentations.Clear();
			node_snippets.Clear();
			root_reference = graph.Nodes.FirstOrDefault(node => node.IsRoot)?.Reference;
			selected_reference = root_reference;
			vertical_layout = use_vertical_layout;

			var layer_groups = graph.Nodes.GroupBy(node => node.Layer)
				.OrderBy(group => group.Key)
				.ToArray();
			if (layer_groups.Length == 0) {
				Clear(empty_message);
				return;
			}

			var heights = new Dictionary<object, double>();
			var column_heights = new Dictionary<int, double>();
			var layer_widths = new Dictionary<int, double>();
			var layer_maximum_heights = new Dictionary<int, double>();
			foreach (var group in layer_groups) {
				double height = 0;
				double width = 0;
				double maximum_height = 0;
				foreach (var node in group) {
					node_presentations[node.Reference] = node;
					var code = snippets.TryGetValue(node.Reference, out var snippet)
						? snippet
						: HighlightedSnippet.Plain(string.Empty);
					var code_block = CreateCodeTextBlock(code);
					double node_height = MeasureNodeHeight(code_block);
					node_snippets[node.Reference] = code;
					heights[node.Reference] = node_height;
					height += node_height + NodeGap;
					width += node_width + NodeGap;
					maximum_height = Math.Max(maximum_height, node_height);
				}
				column_heights[group.Key] = Math.Max(0, height - NodeGap);
				layer_widths[group.Key] = Math.Max(0, width - NodeGap);
				layer_maximum_heights[group.Key] = maximum_height;
			}

			if (vertical_layout) {
				double maximum_layer_width = layer_widths.Values.DefaultIfEmpty(1).Max();
				double y = CanvasMargin;
				foreach (var group in layer_groups) {
					double x = CanvasMargin + (maximum_layer_width - layer_widths[group.Key]) / 2;
					foreach (var node in group) {
						double height = heights[node.Reference];
						node_rects[node.Reference] = new Rect(x, y, node_width, height);
						x += node_width + NodeGap;
					}
					y += layer_maximum_heights[group.Key] + ColumnGap;
				}
				canvas.Width = Math.Max(CanvasMargin * 2 + maximum_layer_width, 640);
				canvas.Height = Math.Max(CanvasMargin * 2 + y - CanvasMargin - ColumnGap, 420);
			}
			else {
				double maximum_column_height = column_heights.Values.DefaultIfEmpty(1).Max();
				for (int column = 0; column < layer_groups.Length; column++) {
					var group = layer_groups[column];
					double x = CanvasMargin + column * (node_width + ColumnGap);
					double y = CanvasMargin + (maximum_column_height - column_heights[group.Key]) / 2;
					foreach (var node in group) {
						double height = heights[node.Reference];
						node_rects[node.Reference] = new Rect(x, y, node_width, height);
						y += height + NodeGap;
					}
				}

				double width = CanvasMargin * 2 + layer_groups.Length * node_width +
					Math.Max(0, layer_groups.Length - 1) * ColumnGap;
				canvas.Width = Math.Max(width, 640);
				canvas.Height = Math.Max(CanvasMargin * 2 + maximum_column_height, 420);
			}

			UpdateExtent();
			canvas.Children.Add(edge_layer);
			foreach (var link in graph.Links)
				AddEdge(link);
			edge_layer.InvalidateDrawing();

			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { CenterRoot(); UpdateVisibleNodes(); }));
		}

		Rect VisibleGraphRect() {
			double width = Math.Max(1, scroll_viewer.ViewportWidth);
			double height = Math.Max(1, scroll_viewer.ViewportHeight);
			var rect = new Rect((scroll_viewer.HorizontalOffset - pan_host.Padding.Left) / Zoom,
				(scroll_viewer.VerticalOffset - pan_host.Padding.Top) / Zoom, width / Zoom, height / Zoom);
			rect.Inflate(80 / Zoom, 80 / Zoom);
			return rect;
		}

		public IReadOnlyList<MethodDef> VisibleMethods() {
			var viewport = VisibleGraphRect();
			return node_rects.Where(pair => pair.Key is MethodDef && pair.Value.IntersectsWith(viewport))
				.OrderBy(pair => Equals(pair.Key, root_reference) ? 0 : 1)
				.ThenBy(pair => Math.Abs(pair.Value.Top - viewport.Top))
				.Select(pair => (MethodDef)pair.Key).ToArray();
		}

		void UpdateVisibleNodes() {
			if (updating_visibility || node_presentations.Count == 0) return;
			updating_visibility = true;
			try {
				var viewport = VisibleGraphRect();
				foreach (var reference in node_borders.Keys.ToArray()) {
					if (Equals(reference, dragged_node_reference) || node_rects[reference].IntersectsWith(viewport)) continue;
					canvas.Children.Remove(node_borders[reference]);
					node_borders.Remove(reference);
				}
				int added = 0;
				bool more = false;
				foreach (var pair in node_rects) {
					if (node_borders.ContainsKey(pair.Key) || !pair.Value.IntersectsWith(viewport)) continue;
					if (++added > 12) { more = true; break; }
					AddNode(node_presentations[pair.Key], CreateCodeTextBlock(node_snippets[pair.Key]));
				}
				if (more && !visibility_update_pending) {
					visibility_update_pending = true;
					Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { visibility_update_pending = false; UpdateVisibleNodes(); }));
				}
			}
			finally { updating_visibility = false; }
			VisibleMethodsChanged?.Invoke(this, EventArgs.Empty);
		}

		public void UpdateSnippets(IReadOnlyDictionary<MethodDef, HighlightedSnippet> snippets) {
			bool geometry_changed = false;
			foreach (var pair in snippets) {
				if (!node_rects.TryGetValue(pair.Key, out var old_rect) ||
					node_snippets.TryGetValue(pair.Key, out var previous) && ReferenceEquals(previous, pair.Value)) continue;
				node_snippets[pair.Key] = pair.Value;
				var code = CreateCodeTextBlock(pair.Value);
				double height = MeasureNodeHeight(code);
				double delta = height - old_rect.Height;
				node_rects[pair.Key] = new Rect(old_rect.Location, new Size(old_rect.Width, height));
				if (cached_borders.TryGetValue(pair.Key, out var border)) {
					var grid = (Grid)border.Child;
					grid.Children.RemoveAt(2); Grid.SetRow(code, 2); grid.Children.Add(code); border.Height = height;
				}
				if (Math.Abs(delta) < 0.5) continue;
				geometry_changed = true;
				foreach (var reference in node_rects.Keys.ToArray()) {
					if (Equals(reference, pair.Key)) continue;
					var rect = node_rects[reference];
					if (Math.Abs(rect.Left - old_rect.Left) > 0.5 || rect.Top <= old_rect.Top) continue;
					rect.Y += delta; node_rects[reference] = rect;
					if (node_borders.TryGetValue(reference, out var moved)) Canvas.SetTop(moved, rect.Top);
				}
			}
			canvas.Height = Math.Max(420, node_rects.Values.Select(rect => rect.Bottom + CanvasMargin).DefaultIfEmpty(420).Max());
			UpdateExtent();
			if (geometry_changed) {
				foreach (var visual in edge_visuals) UpdateEdge(visual);
				edge_layer.InvalidateDrawing();
			}
			UpdateVisibleNodes();
		}

		public void Render(CallGraphSlice slice, IReadOnlyDictionary<MethodDef, string> snippets) =>
			Render(slice, snippets.ToDictionary(pair => pair.Key, pair => HighlightedSnippet.Plain(pair.Value)));

		double MeasureNodeHeight(FrameworkElement text) {
			double available_width = Math.Max(1, node_width - 4);
			text.Measure(new Size(available_width, double.PositiveInfinity));
			return Math.Max(MinimumNodeHeight,
				Math.Ceiling(HeaderHeight + 1 + text.DesiredSize.Height + 4));
		}

		void AddEdge(GraphLinkPresentation link) {
			if (!TryGetEdgePlacement(link, out var placement))
				return;
			bool emphasized = Equals(link.Source, root_reference) || Equals(link.Target, root_reference);
			var path = new Path {
				Data = CreateCurveGeometry(placement),
				StrokeThickness = emphasized ? 1.8 : 1.15,
				Opacity = emphasized ? 0.82 : 0.52,
				ToolTip = link.ToolTip,
				IsHitTestVisible = true,
			};
			if (link.Kind == GraphLinkKind.Exception || link.Kind == GraphLinkKind.PossibleCall)
				path.StrokeDashArray = new DoubleCollection { 4, 3 };
			path.SetResourceReference(Shape.StrokeProperty,
				emphasized || link.Kind == GraphLinkKind.Conditional || link.Kind == GraphLinkKind.Switch
					? "HyperlinkNormal" : "SystemColorsWindowText");
			Panel.SetZIndex(path, 0);

			var arrow = CreateArrow(placement.To, placement.Control2, emphasized);
			FrameworkElement? label = null;
			Vector label_offset = default;
			if (link.CallSiteCount > 1) {
				label = CreateCallCount(link.CallSiteCount);
				label_offset = new Vector(-9, -9);
			}
			else if (!string.IsNullOrEmpty(link.Label)) {
				label = CreateEdgeLabel(link.Label);
				label_offset = new Vector(3, -10);
			}
			if (label is not null) {
				Canvas.SetLeft(label, placement.LabelPoint.X + label_offset.X);
				Canvas.SetTop(label, placement.LabelPoint.Y + label_offset.Y);
			}
			edge_visuals.Add(new EdgeVisual(link, path, arrow, label, label_offset));
		}

		void DrawEdges(DrawingContext context) {
			var accent = (TryFindResource("HyperlinkNormal") as Brush ?? Brushes.DeepSkyBlue).CloneCurrentValue();
			var text = TryFindResource("SystemColorsWindowText") as Brush ?? Brushes.Gainsboro;
			var normal = text.CloneCurrentValue(); normal.Opacity = 0.52; accent.Opacity = 0.82;
			var background = TryFindResource("SystemColorsWindow") as Brush ?? Brushes.Black;
			var normal_pen = new Pen(normal, 1.15);
			var accent_pen = new Pen(accent, 1.8);
			var dashed_pen = new Pen(accent, 1.3) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
			foreach (var edge in edge_visuals) {
				bool emphasized = Equals(edge.Link.Source, root_reference) || Equals(edge.Link.Target, root_reference) || edge.Link.Kind == GraphLinkKind.Conditional || edge.Link.Kind == GraphLinkKind.Switch;
				bool dashed = edge.Link.Kind == GraphLinkKind.PossibleCall || edge.Link.Kind == GraphLinkKind.Exception;
				context.DrawGeometry(null, dashed ? dashed_pen : emphasized ? accent_pen : normal_pen, edge.Curve.Data);
				context.DrawGeometry(emphasized || dashed ? accent : normal, null, edge.Arrow.Data);
				if (edge.Label is not TextBlock label) continue;
				var formatted = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
					new Typeface("Segoe UI"), label.FontSize, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
				var point = new Point(Canvas.GetLeft(label), Canvas.GetTop(label));
				context.DrawRectangle(background, null, new Rect(point, new Size(formatted.Width + 3, formatted.Height)));
				context.DrawText(formatted, point);
			}
		}

		bool TryGetEdgePlacement(GraphLinkPresentation link, out EdgePlacement placement) {
			placement = default;
			if (!node_rects.TryGetValue(link.Source, out var source_rect) ||
				!node_rects.TryGetValue(link.Target, out var target_rect))
				return false;
			Point from;
			Point to;
			Point control1;
			Point control2;
			Point label_point;
			if (vertical_layout && target_rect.Top >= source_rect.Bottom) {
				from = new Point(source_rect.Left + source_rect.Width / 2, source_rect.Bottom);
				to = new Point(target_rect.Left + target_rect.Width / 2, target_rect.Top);
				double delta_y = Math.Max(38, Math.Abs(to.Y - from.Y) * 0.45);
				control1 = new Point(from.X, from.Y + delta_y);
				control2 = new Point(to.X, to.Y - delta_y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (vertical_layout && source_rect.Top >= target_rect.Bottom) {
				from = new Point(source_rect.Left + source_rect.Width / 2, source_rect.Top);
				to = new Point(target_rect.Left + target_rect.Width / 2, target_rect.Bottom);
				double delta_y = Math.Max(38, Math.Abs(to.Y - from.Y) * 0.45);
				control1 = new Point(from.X, from.Y - delta_y);
				control2 = new Point(to.X, to.Y + delta_y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (target_rect.Left >= source_rect.Right) {
				from = new Point(source_rect.Right, source_rect.Top + source_rect.Height / 2);
				to = new Point(target_rect.Left, target_rect.Top + target_rect.Height / 2);
				double delta_x = Math.Max(45, Math.Abs(to.X - from.X) * 0.45);
				control1 = new Point(from.X + delta_x, from.Y);
				control2 = new Point(to.X - delta_x, to.Y);
				label_point = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			}
			else if (source_rect.Left >= target_rect.Right) {
				from = new Point(source_rect.Left, source_rect.Top + source_rect.Height / 2);
				to = new Point(target_rect.Right, target_rect.Top + target_rect.Height / 2);
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
			placement = new EdgePlacement(from, to, control1, control2, label_point);
			return true;
		}

		static Geometry CreateCurveGeometry(EdgePlacement placement) {
			var figure = new PathFigure { StartPoint = placement.From, IsClosed = false, IsFilled = false };
			figure.Segments.Add(new BezierSegment(placement.Control1, placement.Control2, placement.To, true));
			return new PathGeometry(new[] { figure });
		}

		static string BuildLinkToolTip(CallGraphLink link) {
			string offsets = string.Join(", ", link.IlOffsets.Take(10).Select(offset => "IL_" + offset.ToString("X4", CultureInfo.InvariantCulture)));
			if (link.IlOffsets.Count > 10)
				offsets += ", …";
			string resolution = link.IsPotential
				? UiText.Pick("Possible runtime target (static inference)\n", "Возможная runtime-цель (статический вывод)\n")
				: string.Empty;
			return $"{CallGraphIndex.DisplayName(link.Source)} → {CallGraphIndex.DisplayName(link.Target)}\n" + resolution +
				$"{link.CallSiteCount} call site(s): {string.Join(", ", link.OpCodes)}\n{offsets}";
		}

		Path CreateArrow(Point tip, Point from, bool emphasized) {
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
			var arrow = new Path { Data = geometry, Opacity = emphasized ? 0.9 : 0.62, IsHitTestVisible = false };
			arrow.SetResourceReference(Shape.FillProperty, emphasized ? "HyperlinkNormal" : "SystemColorsWindowText");
			Panel.SetZIndex(arrow, 0);
			return arrow;
		}

		TextBlock CreateCallCount(int count) {
			var label = new TextBlock {
				Text = "×" + count.ToString(CultureInfo.InvariantCulture),
				FontSize = 10,
				Padding = new Thickness(3, 0, 3, 1),
				ToolTip = count.ToString(CultureInfo.InvariantCulture) + " call sites",
			};
			label.SetResourceReference(TextBlock.BackgroundProperty, "SystemColorsWindow");
			label.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			Panel.SetZIndex(label, 1);
			return label;
		}

		TextBlock CreateEdgeLabel(string text) {
			var label = new TextBlock {
				Text = text,
				FontSize = 9.5,
				Padding = new Thickness(3, 0, 3, 1),
			};
			label.SetResourceReference(TextBlock.BackgroundProperty, "SystemColorsWindow");
			label.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			Panel.SetZIndex(label, 1);
			return label;
		}

		void UpdateConnectedEdges(object reference) {
			foreach (var visual in edge_visuals) {
				if (!Equals(visual.Link.Source, reference) && !Equals(visual.Link.Target, reference))
					continue;
				UpdateEdge(visual);
			}
			edge_layer.InvalidateDrawing();
		}

		void UpdateEdge(EdgeVisual visual) {
			if (!TryGetEdgePlacement(visual.Link, out var placement)) return;
			visual.Curve.Data = CreateCurveGeometry(placement);
			visual.Arrow.Data = CreateArrow(placement.To, placement.Control2,
				Equals(visual.Link.Source, root_reference) || Equals(visual.Link.Target, root_reference)).Data;
			if (visual.Label is not null) {
				Canvas.SetLeft(visual.Label, placement.LabelPoint.X + visual.LabelOffset.X);
				Canvas.SetTop(visual.Label, placement.LabelPoint.Y + visual.LabelOffset.Y);
			}
		}

		void AddNode(GraphNodePresentation node, GraphCodeText code_text) {
			Rect rect = node_rects[node.Reference];
			if (cached_borders.TryGetValue(node.Reference, out var cached)) {
				node_borders[node.Reference] = cached;
				cached.BorderThickness = Equals(selected_reference, node.Reference) || node.IsRoot ? new Thickness(2) : new Thickness(1);
				cached.SetResourceReference(Border.BorderBrushProperty, Equals(selected_reference, node.Reference) && !node.IsRoot ? "ToolBarButtonHoverBorder" : node.IsRoot ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");
				canvas.Children.Add(cached); Canvas.SetLeft(cached, rect.Left); Canvas.SetTop(cached, rect.Top); return;
			}
			var border = new Border {
				Width = rect.Width,
				Height = rect.Height,
				BorderThickness = node.IsRoot ? new Thickness(2) : new Thickness(1),
				CornerRadius = new CornerRadius(2),
				SnapsToDevicePixels = true,
				Tag = node.Reference,
				ToolTip = node.ToolTip,
			};
			border.SetResourceReference(Border.BackgroundProperty, "SystemColorsWindow");
			border.SetResourceReference(Border.BorderBrushProperty,
				Equals(node.Reference, selected_reference) && !node.IsRoot ? "ToolBarButtonHoverBorder" :
				node.IsRoot ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");

			var grid = new Grid();
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.Children.Add(CreateHeader(node));

			var separator = new Border { Height = 1 };
			separator.SetResourceReference(Border.BackgroundProperty, "CommonControlsTextBoxBorder");
			Grid.SetRow(separator, 1);
			grid.Children.Add(separator);

			Grid.SetRow(code_text, 2);
			grid.Children.Add(code_text);
			border.Child = grid;

			border.MouseLeftButtonDown += OnNodeMouseLeftButtonDown;
			var lazy_menu = new ContextMenu();
			bool menu_created = false;
			lazy_menu.Opened += (_, __) => {
				if (menu_created) return;
				menu_created = true;
				var populated = CreateNodeContextMenu(node);
				while (populated.Items.Count != 0) { var item = populated.Items[0]; populated.Items.RemoveAt(0); lazy_menu.Items.Add(item); }
			};
			border.ContextMenu = lazy_menu;
			node_borders[node.Reference] = border;
			cached_borders[node.Reference] = border;
			Panel.SetZIndex(border, 2);
			canvas.Children.Add(border);
			Canvas.SetLeft(border, rect.Left);
			Canvas.SetTop(border, rect.Top);
		}

		GraphCodeText CreateCodeTextBlock(HighlightedSnippet code) => new GraphCodeText(code, theme_service,
			syntax_highlighting, wrap_long_lines, reference => {
				if (CallGraphControl.ResolveMethod(reference) is MethodDef method)
					RootRequested?.Invoke(this, new GraphReferenceEventArgs(method));
			}, CreateMethodReferenceContextMenu);

		FrameworkElement CreateHeader(GraphNodePresentation node) {
			var panel = new Grid { MinHeight = 47 };
			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.SetResourceReference(Panel.BackgroundProperty,
				node.IsRoot ? "ToolBarButtonChecked" : "EnvironmentBackground");

			var name = new TextBlock {
				Text = node.Title,
				FontWeight = FontWeights.SemiBold,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(8, 5, 8, 0),
			};
			name.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			if (theme_service is not null && node.Reference is MethodDef method) {
				var color = theme_service.Theme.GetTextColor((ColorType)((uint)ColorType.Text + (uint)(method.IsStatic ? TextColor.StaticMethod : TextColor.InstanceMethod)));
				if (color.Foreground is not null) name.Foreground = color.Foreground;
			}
			panel.Children.Add(name);

			string subtitle = node.Subtitle;
			if (node.IsExternal)
				subtitle += "   " + UiText.Pick("[external]", "[внешний]");
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
			return panel;
		}

		ContextMenu CreateNodeContextMenu(GraphNodePresentation node) {
			var menu = new ContextMenu();
			if (node.Reference is ControlFlowBlock block) {
				var open_method = new MenuItem { Header = UiText.OpenMethodSource, FontWeight = FontWeights.SemiBold };
				open_method.Click += (_, __) => NavigateRequested?.Invoke(this, new GraphReferenceEventArgs(block.Method));
				menu.Items.Add(open_method);
				menu.Items.Add(new Separator());
				var copy_block = new MenuItem { Header = UiText.CopyBlockIl };
				copy_block.Click += (_, __) => Clipboard.SetText(block.Snippet.Text);
				menu.Items.Add(copy_block);
				return menu;
			}
			var root = new MenuItem { Header = UiText.ExploreHere, FontWeight = FontWeights.SemiBold };
			root.Click += (_, __) => RootRequested?.Invoke(this, new GraphReferenceEventArgs(node.Reference));
			menu.Items.Add(root);
			var open = new MenuItem { Header = UiText.OpenSource };
			open.Click += (_, __) => NavigateRequested?.Invoke(this, new GraphReferenceEventArgs(node.Reference));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = UiText.CopyFullName };
			copy.Click += (_, __) => Clipboard.SetText(node.CopyText);
			menu.Items.Add(copy);
			return menu;
		}

		ContextMenu CreateMethodReferenceContextMenu(IMethod reference) {
			var menu = new ContextMenu();
			var explore = new MenuItem {
				Header = vertical_layout ? UiText.ShowMethodInCfg : UiText.ExploreHere,
				FontWeight = FontWeights.SemiBold,
			};
			explore.Click += (_, __) => {
				if (CallGraphControl.ResolveMethod(reference) is MethodDef definition)
					RootRequested?.Invoke(this, new GraphReferenceEventArgs(definition));
			};
			menu.Items.Add(explore);
			var open = new MenuItem { Header = UiText.OpenSource };
			open.Click += (_, __) => NavigateRequested?.Invoke(this, new GraphReferenceEventArgs(reference));
			menu.Items.Add(open);
			menu.Items.Add(new Separator());
			var copy = new MenuItem { Header = UiText.CopyFullName };
			copy.Click += (_, __) => Clipboard.SetText(reference.FullName);
			menu.Items.Add(copy);
			return menu;
		}

		void OnNodeMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
			if (sender is not Border border || border.Tag is not object reference)
				return;
			if (e.ClickCount == 2 && reference is not ControlFlowBlock) {
				RootRequested?.Invoke(this, new GraphReferenceEventArgs(reference));
				e.Handled = true;
				return;
			}
			SelectNode(reference);
			e.Handled = false;
		}

		void SelectNode(object reference) {
			if (Equals(selected_reference, reference))
				return;
			if (selected_reference is not null && node_borders.TryGetValue(selected_reference, out var old_border)) {
				old_border.BorderThickness = Equals(old_border.Tag, root_reference) ? new Thickness(2) : new Thickness(1);
				old_border.SetResourceReference(Border.BorderBrushProperty,
					Equals(old_border.Tag, root_reference) ? "HyperlinkNormal" : "CommonControlsTextBoxBorder");
			}
			selected_reference = reference;
			if (node_borders.TryGetValue(reference, out var border)) {
				border.BorderThickness = new Thickness(2);
				border.SetResourceReference(Border.BorderBrushProperty, "ToolBarButtonHoverBorder");
			}
			SelectionChanged?.Invoke(this, new GraphReferenceEventArgs(reference));
		}

		public void CenterRoot() {
			if (root_reference is null)
				return;
			CenterReference(root_reference);
		}

		public bool FocusReference(object reference) {
			if (!node_rects.ContainsKey(reference))
				return false;
			SelectNode(reference);
			CenterReference(reference);
			return true;
		}

		void CenterReference(object reference) {
			if (!node_rects.TryGetValue(reference, out var rect))
				return;
			double center_x = pan_host.Padding.Left + (rect.Left + rect.Width / 2) * Zoom;
			double center_y = pan_host.Padding.Top + (rect.Top + rect.Height / 2) * Zoom;
			scroll_viewer.ScrollToHorizontalOffset(center_x - scroll_viewer.ViewportWidth / 2);
			scroll_viewer.ScrollToVerticalOffset(center_y - scroll_viewer.ViewportHeight / 2);
		}

		void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) {
			if (e.Delta == 0)
				return;

			
			
			
			Point pointer = e.GetPosition(scroll_viewer);
			double old_zoom = Zoom;
			double content_x = (scroll_viewer.HorizontalOffset + pointer.X - pan_host.Padding.Left) / old_zoom;
			double content_y = (scroll_viewer.VerticalOffset + pointer.Y - pan_host.Padding.Top) / old_zoom;
			double wheel_notches = e.Delta / 120.0;
			Zoom = old_zoom * Math.Pow(1.18, wheel_notches);
			double new_zoom = Zoom;

			
			
			scroll_viewer.UpdateLayout();
			scroll_viewer.ScrollToHorizontalOffset(pan_host.Padding.Left + content_x * new_zoom - pointer.X);
			scroll_viewer.ScrollToVerticalOffset(pan_host.Padding.Top + content_y * new_zoom - pointer.Y);
			e.Handled = true;
		}

		void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) {
			if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle)
				return;
			pan_button = e.ChangedButton;
			is_pan_candidate = true;
			is_panning = false;
			is_node_dragging = false;
			pan_start = e.GetPosition(this);
			pan_horizontal_offset = scroll_viewer.HorizontalOffset;
			pan_vertical_offset = scroll_viewer.VerticalOffset;
			dragged_node_reference = e.ChangedButton == MouseButton.Left && e.ClickCount == 1
				? FindNodeReference(e.OriginalSource as DependencyObject)
				: null;
			if (dragged_node_reference is not null && node_rects.TryGetValue(dragged_node_reference, out var rect)) {
				dragged_node_start = rect;
				dragged_node_pointer_start = e.GetPosition(canvas);
			}
			if (pan_button == MouseButton.Middle)
				e.Handled = true;
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e) {
			if (!is_pan_candidate)
				return;
			bool button_pressed = pan_button == MouseButton.Left
				? e.LeftButton == MouseButtonState.Pressed
				: e.MiddleButton == MouseButtonState.Pressed;
			if (!button_pressed) {
				EndPan();
				return;
			}
			Point current = e.GetPosition(this);
			if (dragged_node_reference is not null) {
				Vector screen_delta = current - pan_start;
				if (!is_node_dragging && Math.Abs(screen_delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
					Math.Abs(screen_delta.Y) < SystemParameters.MinimumVerticalDragDistance)
					return;
				if (!is_node_dragging) {
					is_node_dragging = true;
					Mouse.Capture(this);
					Cursor = Cursors.SizeAll;
					if (node_borders.TryGetValue(dragged_node_reference, out var dragged_border))
						Panel.SetZIndex(dragged_border, 4);
				}
				Point canvas_point = e.GetPosition(canvas);
				Vector delta = canvas_point - dragged_node_pointer_start;
				MoveNode(dragged_node_reference,
					Math.Max(0, dragged_node_start.Left + delta.X),
					Math.Max(0, dragged_node_start.Top + delta.Y));
				e.Handled = true;
				return;
			}
			if (!is_panning) {
				Vector delta = current - pan_start;
				if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
					Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
					return;
				is_panning = true;
				Mouse.Capture(this);
				Cursor = Cursors.ScrollAll;
			}
			scroll_viewer.ScrollToHorizontalOffset(pan_horizontal_offset - (current.X - pan_start.X));
			scroll_viewer.ScrollToVerticalOffset(pan_vertical_offset - (current.Y - pan_start.Y));
			e.Handled = true;
		}

		void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) {
			if (!is_pan_candidate || e.ChangedButton != pan_button)
				return;
			bool handled = is_panning || is_node_dragging || pan_button == MouseButton.Middle;
			EndPan();
			e.Handled = handled;
		}

		void EndPan() {
			bool had_capture = IsMouseCaptured;
			if (dragged_node_reference is not null && node_borders.TryGetValue(dragged_node_reference, out var dragged_border))
				Panel.SetZIndex(dragged_border, 2);
			is_pan_candidate = false;
			is_panning = false;
			is_node_dragging = false;
			dragged_node_reference = null;
			Cursor = Cursors.Arrow;
			if (had_capture)
				Mouse.Capture(null);
		}

		object? FindNodeReference(DependencyObject? source) {
			var current = source;
			while (current is not null) {
				if (current is Border border && border.Tag is not null &&
					node_borders.TryGetValue(border.Tag, out var node_border) && ReferenceEquals(border, node_border))
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

		void MoveNode(object reference, double left, double top) {
			if (!node_rects.TryGetValue(reference, out var old_rect) || !node_borders.TryGetValue(reference, out var border))
				return;
			var rect = new Rect(left, top, old_rect.Width, old_rect.Height);
			node_rects[reference] = rect;
			Canvas.SetLeft(border, rect.Left);
			Canvas.SetTop(border, rect.Top);
			canvas.Width = Math.Max(canvas.Width, rect.Right + CanvasMargin);
			canvas.Height = Math.Max(canvas.Height, rect.Bottom + CanvasMargin);
			UpdateExtent();
			UpdateConnectedEdges(reference);
		}
	}
}
