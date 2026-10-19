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
		const double ColumnGap = 115;
		const double NodeGap = 30;
		const double CanvasMargin = 60;
		const double MinimumHorizontalPanSpace = 420;
		const double MinimumVerticalPanSpace = 260;
		const double DefaultNodeWidth = 430;
		const double HeaderHeight = 47;
		const double CodeVerticalMargin = 12;
		const double MinimumNodeHeight = 78;

		readonly ScrollViewer scroll_viewer;
		readonly Border pan_host;
		readonly Canvas canvas;
		readonly ScaleTransform scale_transform;
		IThemeService? theme_service;
		readonly Dictionary<object, Border> node_borders = new Dictionary<object, Border>();
		readonly Dictionary<object, Rect> node_rects = new Dictionary<object, Rect>();
		readonly List<(Run Run, TextColor Color)> colored_runs = new List<(Run, TextColor)>();
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

		public event EventHandler<GraphReferenceEventArgs>? NavigateRequested;
		public event EventHandler<GraphReferenceEventArgs>? RootRequested;
		public event EventHandler<GraphReferenceEventArgs>? SelectionChanged;

		public double Zoom {
			get => scale_transform.ScaleX;
			set {
				double next = Math.Max(0.01, Math.Min(4.0, value));
				if (Math.Abs(next - scale_transform.ScaleX) < 0.001)
					return;
				scale_transform.ScaleX = next;
				scale_transform.ScaleY = next;
			}
		}

		public CallGraphSurface() : this(null) { }

		internal CallGraphSurface(IThemeService? theme_service) {
			this.theme_service = theme_service;
			Focusable = true;
			canvas = new Canvas { MinWidth = 1, MinHeight = 1, ClipToBounds = false };
			canvas.SetResourceReference(Panel.BackgroundProperty, "SystemColorsWindow");
			
			
			TextOptions.SetTextRenderingMode(canvas, TextRenderingMode.Grayscale);
			TextOptions.SetTextFormattingMode(canvas, TextFormattingMode.Ideal);
			RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.HighQuality);
			scale_transform = new ScaleTransform(1, 1);
			canvas.LayoutTransform = scale_transform;
			pan_host = new Border {
				Child = canvas,
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
			canvas.Children.Clear();
			node_borders.Clear();
			node_rects.Clear();
			colored_runs.Clear();
			root_reference = selected_reference = null;
			canvas.Width = Math.Max(1, ActualWidth);
			canvas.Height = Math.Max(1, ActualHeight);
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
				node.Method.DeclaringType?.FullName ?? "<Module>",
				CallGraphIndex.Signature(node.Method),
				CallGraphIndex.DisplayName(node.Method))).ToArray();
			var links = slice.Links.Select(link => new GraphLinkPresentation(
				link.Source, link.Target, link.CallSiteCount, BuildLinkToolTip(link),
				string.Empty, link.IsPotential ? GraphLinkKind.PossibleCall : GraphLinkKind.Call)).ToArray();
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
			node_rects.Clear();
			colored_runs.Clear();
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
			var code_blocks = new Dictionary<object, TextBlock>();
			var column_heights = new Dictionary<int, double>();
			var layer_widths = new Dictionary<int, double>();
			var layer_maximum_heights = new Dictionary<int, double>();
			foreach (var group in layer_groups) {
				double height = 0;
				double width = 0;
				double maximum_height = 0;
				foreach (var node in group) {
					var code = snippets.TryGetValue(node.Reference, out var snippet)
						? snippet
						: HighlightedSnippet.Plain(string.Empty);
					var code_block = CreateCodeTextBlock(code);
					double node_height = MeasureNodeHeight(code_block);
					code_blocks[node.Reference] = code_block;
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

			foreach (var link in graph.Links)
				AddEdge(link);
			foreach (var node in graph.Nodes)
				AddNode(node, code_blocks[node.Reference]);

			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CenterRoot));
		}

		public void Render(CallGraphSlice slice, IReadOnlyDictionary<MethodDef, string> snippets) =>
			Render(slice, snippets.ToDictionary(pair => pair.Key, pair => HighlightedSnippet.Plain(pair.Value)));

		double MeasureNodeHeight(TextBlock text) {
			double available_width = Math.Max(1, node_width - 16);
			text.Measure(new Size(available_width, double.PositiveInfinity));
			return Math.Max(MinimumNodeHeight,
				Math.Ceiling(HeaderHeight + 1 + CodeVerticalMargin + text.DesiredSize.Height + 1));
		}

		void AddEdge(GraphLinkPresentation link) {
			if (!node_rects.TryGetValue(link.Source, out var source_rect) ||
				!node_rects.TryGetValue(link.Target, out var target_rect))
				return;

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

			var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
			figure.Segments.Add(new BezierSegment(control1, control2, to, true));
			var geometry = new PathGeometry(new[] { figure });
			var path = new Path {
				Data = geometry,
				StrokeThickness = Equals(link.Source, root_reference) || Equals(link.Target, root_reference) ? 1.8 : 1.15,
				Opacity = Equals(link.Source, root_reference) || Equals(link.Target, root_reference) ? 0.82 : 0.52,
				ToolTip = link.ToolTip,
				IsHitTestVisible = true,
			};
			if (link.Kind == GraphLinkKind.Exception || link.Kind == GraphLinkKind.PossibleCall)
				path.StrokeDashArray = new DoubleCollection { 4, 3 };
			path.SetResourceReference(Shape.StrokeProperty,
				Equals(link.Source, root_reference) || Equals(link.Target, root_reference) ||
				link.Kind == GraphLinkKind.Conditional || link.Kind == GraphLinkKind.Switch
					? "HyperlinkNormal" : "SystemColorsWindowText");
			Panel.SetZIndex(path, 0);
			canvas.Children.Add(path);

			AddArrow(to, control2, Equals(link.Source, root_reference) || Equals(link.Target, root_reference));
			if (link.CallSiteCount > 1)
				AddCallCount(link.CallSiteCount, label_point);
			else if (!string.IsNullOrEmpty(link.Label))
				AddEdgeLabel(link.Label, label_point);
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

		void AddArrow(Point tip, Point from, bool emphasized) {
			Vector vector = tip - from;
			if (vector.LengthSquared < 1)
				return;
			vector.Normalize();
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
			canvas.Children.Add(arrow);
		}

		void AddCallCount(int count, Point point) {
			var label = new TextBlock {
				Text = "×" + count.ToString(CultureInfo.InvariantCulture),
				FontSize = 10,
				Padding = new Thickness(3, 0, 3, 1),
				ToolTip = count.ToString(CultureInfo.InvariantCulture) + " call sites",
			};
			label.SetResourceReference(TextBlock.BackgroundProperty, "SystemColorsWindow");
			label.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			Panel.SetZIndex(label, 1);
			canvas.Children.Add(label);
			Canvas.SetLeft(label, point.X - 9);
			Canvas.SetTop(label, point.Y - 9);
		}

		void AddEdgeLabel(string text, Point point) {
			var label = new TextBlock {
				Text = text,
				FontSize = 9.5,
				Padding = new Thickness(3, 0, 3, 1),
			};
			label.SetResourceReference(TextBlock.BackgroundProperty, "SystemColorsWindow");
			label.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			Panel.SetZIndex(label, 1);
			canvas.Children.Add(label);
			Canvas.SetLeft(label, point.X + 3);
			Canvas.SetTop(label, point.Y - 10);
		}

		void AddNode(GraphNodePresentation node, TextBlock code_text) {
			Rect rect = node_rects[node.Reference];
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
			border.ContextMenu = CreateNodeContextMenu(node);
			node_borders[node.Reference] = border;
			Panel.SetZIndex(border, 2);
			canvas.Children.Add(border);
			Canvas.SetLeft(border, rect.Left);
			Canvas.SetTop(border, rect.Top);
		}

		TextBlock CreateCodeTextBlock(HighlightedSnippet code) {
			var code_text = new TextBlock {
				Margin = new Thickness(8, 6, 8, 6),
				FontFamily = new FontFamily("Consolas"),
				FontSize = 11.5,
				TextWrapping = wrap_long_lines ? TextWrapping.Wrap : TextWrapping.NoWrap,
				Focusable = false,
			};
			code_text.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText");
			AddCodeInlines(code_text, code);
			TextOptions.SetTextRenderingMode(code_text, TextRenderingMode.Grayscale);
			TextOptions.SetTextHintingMode(code_text, TextHintingMode.Animated);
			return code_text;
		}

		void AddCodeInlines(TextBlock text_block, HighlightedSnippet snippet) {
			if (snippet.Spans.Count == 0) {
				text_block.Text = snippet.Text;
				return;
			}

			int position = 0;
			foreach (var span in snippet.Spans.OrderBy(item => item.Start)) {
				if (span.Start < position || span.Start >= snippet.Text.Length || span.Length <= 0)
					continue;
				if (span.Start > position)
					text_block.Inlines.Add(new Run(snippet.Text.Substring(position, span.Start - position)));
				int length = Math.Min(span.Length, snippet.Text.Length - span.Start);
				var run = new Run(snippet.Text.Substring(span.Start, length));
				if (syntax_highlighting)
					ApplySyntaxColor(run, span.Color);
				if (span.Reference is IMethod method_reference) {
					run.Cursor = Cursors.Hand;
					run.ToolTip = UiText.DoubleClickExplore(method_reference.FullName);
					run.ContextMenu = CreateMethodReferenceContextMenu(method_reference);
					run.MouseEnter += (_, __) => run.TextDecorations = TextDecorations.Underline;
					run.MouseLeave += (_, __) => run.TextDecorations = null;
					run.MouseLeftButtonDown += (_, e) => {
						if (e.ClickCount != 2)
							return;
						if (CallGraphControl.ResolveMethod(method_reference) is MethodDef definition)
							RootRequested?.Invoke(this, new GraphReferenceEventArgs(definition));
						e.Handled = true;
					};
				}
				if (syntax_highlighting)
					colored_runs.Add((run, span.Color));
				text_block.Inlines.Add(run);
				position = span.Start + length;
			}
			if (position < snippet.Text.Length)
				text_block.Inlines.Add(new Run(snippet.Text.Substring(position)));
		}

		void ApplySyntaxColor(Run run, TextColor text_color) {
			if (theme_service is null)
				return;
			uint color_value = (uint)ColorType.Text + (uint)text_color;
			var color = theme_service.Theme.GetTextColor((ColorType)color_value);
			if (color.Foreground is not null)
				run.Foreground = color.Foreground;
			if (color.FontWeight is not null)
				run.FontWeight = color.FontWeight.Value;
			if (color.FontStyle is not null)
				run.FontStyle = color.FontStyle.Value;
		}

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
			pan_start = e.GetPosition(this);
			pan_horizontal_offset = scroll_viewer.HorizontalOffset;
			pan_vertical_offset = scroll_viewer.VerticalOffset;
			
			
			
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
			bool handled = is_panning || pan_button == MouseButton.Middle;
			EndPan();
			e.Handled = handled;
		}

		void EndPan() {
			bool had_capture = IsMouseCaptured;
			is_pan_candidate = false;
			is_panning = false;
			Cursor = Cursors.Arrow;
			if (had_capture)
				Mouse.Capture(null);
		}
	}
}
