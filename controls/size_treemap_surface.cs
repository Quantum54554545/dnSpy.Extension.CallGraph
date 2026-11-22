using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	sealed class size_treemap_surface : FrameworkElement {
		static readonly Color[] palette = { Color.FromRgb(49, 105, 153), Color.FromRgb(32, 120, 121), Color.FromRgb(69, 119, 71), Color.FromRgb(116, 113, 43), Color.FromRgb(148, 99, 40), Color.FromRgb(157, 76, 55), Color.FromRgb(147, 63, 81), Color.FromRgb(133, 66, 113), Color.FromRgb(110, 78, 153), Color.FromRgb(74, 89, 158), Color.FromRgb(86, 110, 127), Color.FromRgb(49, 126, 103) };
		static readonly SolidColorBrush[] swatches = palette.Select(color => { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }).ToArray();
		readonly DispatcherTimer resize_timer;
		CancellationTokenSource? operation;
		size_item? current;
		size_item? selected;
		size_item? hovered;
		size_tile[] tiles = Array.Empty<size_tile>();
		DrawingGroup? drawing;
		bool closed;
		public event Action<size_item>? selected_item;
		public event Action<size_item>? opened_item;
		public Func<size_item, string>? describe_item { get; set; }
		internal int tile_count => this.tiles.Length;
		public size_treemap_surface() {
			Focusable = true; ClipToBounds = true;
			this.resize_timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
			this.resize_timer.Tick += (_, __) => { this.resize_timer.Stop(); this.rebuild(); };
			SizeChanged += (_, __) => { this.operation?.Cancel(); this.tiles = Array.Empty<size_tile>(); this.drawing = null; InvalidateVisual(); this.resize_timer.Stop(); this.resize_timer.Start(); };
		}
		public void show(size_item item) { this.current = item; this.selected = null; this.rebuild(); }
		public void select(size_item? item) { this.selected = item; InvalidateVisual(); }
		public void refresh_theme() { this.drawing = null; InvalidateVisual(); }
		public void close() { this.closed = true; this.resize_timer.Stop(); this.operation?.Cancel(); this.operation?.Dispose(); this.operation = null; this.tiles = Array.Empty<size_tile>(); this.drawing = null; this.current = this.selected = this.hovered = null; this.describe_item = null; ToolTip = null; }
		async void rebuild() {
			if (this.closed) return;
			this.operation?.Cancel(); this.operation?.Dispose(); this.operation = new CancellationTokenSource(); var token = this.operation.Token; var item = this.current;
			this.tiles = Array.Empty<size_tile>(); this.drawing = null; this.hovered = null; ToolTip = null; InvalidateVisual();
			if (item is null || ActualWidth <= 0 || ActualHeight <= 0) return;
			var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
			try { var next = await Task.Run(() => size_treemap_layout.build(item, bounds, token), token); if (token.IsCancellationRequested || this.closed || !ReferenceEquals(item, this.current)) return; this.tiles = next; this.drawing = null; InvalidateVisual(); }
			catch (OperationCanceledException) { }
			catch (Exception error) { if (!this.closed && !token.IsCancellationRequested) ToolTip = error.Message; }
		}
		static int color_index(size_item item) { uint result = 2166136261; for (size_item? current = item; current is not null; current = current.parent) { foreach (char character in current.key.Length != 0 ? current.key : current.name) result = unchecked((result ^ character) * 16777619); result = unchecked((result ^ 0xFFFF) * 16777619); } result ^= result >> 16; return (int)(result % (uint)palette.Length); }
		internal static Color item_color(size_item item) => palette[color_index(item)];
		internal static SolidColorBrush item_swatch(size_item item) => swatches[color_index(item)];
		static bool contains(size_item parent, size_item item) { for (size_item? current = item; current is not null; current = current.parent) if (ReferenceEquals(parent, current)) return true; return false; }
		Brush tile_brush(size_item item) {
			var color = item_color(item);
			Color tint(double scale) => Color.FromRgb((byte)Math.Clamp(color.R * scale, 0, 255), (byte)Math.Clamp(color.G * scale, 0, 255), (byte)Math.Clamp(color.B * scale, 0, 255));
			var brush = new LinearGradientBrush(tint(1.05), tint(0.9), 90); brush.Freeze(); return brush;
		}
		protected override void OnRender(DrawingContext context) {
			base.OnRender(context); context.DrawRectangle(TryFindResource("SystemColorsWindow") as Brush ?? Brushes.Transparent, null, new Rect(RenderSize));
			if (this.drawing is null) {
				var edge = new Pen(new SolidColorBrush(Color.FromArgb(150, 22, 25, 29)), 0.7); edge.Freeze();
				var next = new DrawingGroup(); using (var canvas = next.Open()) foreach (var tile in this.tiles) {
					var rect = tile.rectangle; if (rect.Width < 0.2 || rect.Height < 0.2) continue;
					canvas.DrawRectangle(this.tile_brush(tile.item), edge, rect);
					if (rect.Width < 48 || rect.Height < 20) continue;
					string name = tile.item.method_references.Count == 1 ? tile.item.method_references[0].type_name + "::" + tile.item.method_references[0].name : tile.item.name;
					string title = file_size_analysis.label(name) + (rect.Height < 46 ? " · " : "\n") + tile.item.size_text;
					var text = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, rect.Width - 10), MaxTextHeight = Math.Max(1, rect.Height - 8), Trimming = TextTrimming.CharacterEllipsis };
					canvas.PushClip(new RectangleGeometry(rect)); canvas.DrawText(text, new Point(rect.X + 5, rect.Y + 3)); canvas.Pop();
				}
				next.Freeze(); this.drawing = next;
			}
			context.DrawDrawing(this.drawing);
			if (this.selected is not null && this.selected != this.current) foreach (var tile in this.tiles.Where(tile => contains(this.selected, tile.item) || contains(tile.item, this.selected))) { var rect = tile.rectangle; rect.Inflate(-1, -1); if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0) context.DrawRectangle(null, new Pen(TryFindResource("SystemColorsHighlight") as Brush ?? Brushes.DeepSkyBlue, 2), rect); }
		}
		internal size_item? hit(Point point) => this.tiles.LastOrDefault(tile => tile.rectangle.Contains(point))?.item;
		protected override void OnMouseMove(MouseEventArgs args) { base.OnMouseMove(args); var item = this.hit(args.GetPosition(this)); if (item == this.hovered) return; this.hovered = item; string name = item is null ? "" : this.describe_item?.Invoke(item) ?? (item.method_references.Count == 1 ? item.method_references[0].title : item.name); ToolTip = item is null ? null : name + "\n" + item.bytes_text + " B · " + item.size_text + "\n" + item.detail; Cursor = item is not null && (item.children.Count > 0 || item.method_references.Count > 0) ? Cursors.Hand : Cursors.Arrow; }
		protected override void OnMouseLeave(MouseEventArgs args) { base.OnMouseLeave(args); this.hovered = null; ToolTip = null; Cursor = Cursors.Arrow; }
		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args) { base.OnMouseLeftButtonDown(args); Focus(); if (this.hit(args.GetPosition(this)) is not size_item item) return; this.select(item); this.selected_item?.Invoke(item); if (args.ClickCount == 2) this.opened_item?.Invoke(item); args.Handled = true; }
	}
}
