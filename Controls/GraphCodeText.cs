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
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph.Controls {
	sealed class GraphCodeText : FrameworkElement {
		readonly HighlightedSnippet snippet;
		readonly IThemeService? theme_service;
		readonly bool highlight;
		readonly bool wrap;
		readonly Action<IMethod> explore;
		readonly Func<IMethod, ContextMenu> create_menu;
		readonly Dictionary<IMethod, List<Geometry>> link_regions = new Dictionary<IMethod, List<Geometry>>();
		FormattedText? text;
		double text_width;
		bool styled;
		bool links_built;

		public GraphCodeText(HighlightedSnippet snippet, IThemeService? theme_service, bool highlight, bool wrap,
			Action<IMethod> explore, Func<IMethod, ContextMenu> create_menu) {
			this.snippet = snippet; this.theme_service = theme_service; this.highlight = highlight; this.wrap = wrap;
			this.explore = explore; this.create_menu = create_menu;
			Margin = new Thickness(8, 6, 8, 6);
			ClipToBounds = true;
			TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
			TextOptions.SetTextHintingMode(this, TextHintingMode.Animated);
		}

		FormattedText GetText(double width) {
			width = Math.Max(1, width);
			if (text is not null && Math.Abs(text_width - width) < 0.1) return text;
			text_width = width; styled = links_built = false; link_regions.Clear();
			text = new FormattedText(snippet.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Consolas"), 11.5, TryFindResource("SystemColorsWindowText") as Brush ?? Brushes.Gainsboro,
				VisualTreeHelper.GetDpi(this).PixelsPerDip);
			text.MaxTextWidth = wrap ? width : 1000000;
			return text;
		}

		protected override Size MeasureOverride(Size availableSize) {
			var value = GetText(availableSize.Width);
			return new Size(Math.Min(availableSize.Width, value.WidthIncludingTrailingWhitespace), value.Height);
		}

		protected override void OnRender(DrawingContext drawingContext) {
			var value = GetText(RenderSize.Width);
			if (!styled && highlight && theme_service is not null) {
				foreach (var span in snippet.Spans) {
					if (span.Start < 0 || span.Start >= snippet.Text.Length || span.Length <= 0) continue;
					int length = Math.Min(span.Length, snippet.Text.Length - span.Start);
					var color = theme_service.Theme.GetTextColor((ColorType)((uint)ColorType.Text + (uint)span.Color));
					if (color.Foreground is not null) value.SetForegroundBrush(color.Foreground, span.Start, length);
				}
				styled = true;
			}
			drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
			drawingContext.DrawText(value, new Point());
		}

		IMethod? ReferenceAt(Point position) {
			if (!links_built) {
				var value = GetText(RenderSize.Width);
				foreach (var span in snippet.Spans) {
					if (span.Reference is not IMethod method || span.Start < 0 || span.Start >= snippet.Text.Length || span.Length <= 0) continue;
					var geometry = value.BuildHighlightGeometry(new Point(), span.Start, Math.Min(span.Length, snippet.Text.Length - span.Start));
					if (geometry is null) continue;
					geometry.Freeze();
					if (!link_regions.TryGetValue(method, out var regions)) link_regions[method] = regions = new List<Geometry>();
					regions.Add(geometry);
				}
				links_built = true;
			}
			return link_regions.FirstOrDefault(pair => pair.Value.Any(region => region.FillContains(position))).Key;
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
			if (e.ClickCount == 2 && ReferenceAt(e.GetPosition(this)) is IMethod reference) {
				explore(reference); e.Handled = true;
			}
			base.OnMouseLeftButtonDown(e);
		}
		protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) {
			if (ReferenceAt(e.GetPosition(this)) is IMethod reference) {
				var menu = create_menu(reference); menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true;
			}
			base.OnMouseRightButtonDown(e);
		}
	}
}
