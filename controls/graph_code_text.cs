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
	sealed class graph_code_text : FrameworkElement {
		readonly highlighted_snippet snippet;
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

		public graph_code_text(highlighted_snippet snippet, IThemeService? theme_service, bool highlight, bool wrap,
			Action<IMethod> explore, Func<IMethod, ContextMenu> create_menu) {
			this.snippet = snippet; this.theme_service = theme_service; this.highlight = highlight; this.wrap = wrap;
			this.explore = explore; this.create_menu = create_menu;
			Margin = new Thickness(8, 6, 8, 6);
			ClipToBounds = true;
			TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
			TextOptions.SetTextHintingMode(this, TextHintingMode.Animated);
		}

		FormattedText get_text(double width) {
			width = Math.Max(1, width);
			if (this.text is not null && Math.Abs(this.text_width - width) < 0.1) return this.text;
			this.text_width = width; this.styled = this.links_built = false; this.link_regions.Clear();
			this.text = new FormattedText(this.snippet.text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				new Typeface("Consolas"), 11.5, TryFindResource("SystemColorsWindowText") as Brush ?? Brushes.Gainsboro,
				VisualTreeHelper.GetDpi(this).PixelsPerDip);
			this.text.MaxTextWidth = this.wrap ? width : 1000000;
			return this.text;
		}

		protected override Size MeasureOverride(Size available_size) {
			var value = this.get_text(available_size.Width);
			return new Size(Math.Min(available_size.Width, value.WidthIncludingTrailingWhitespace), value.Height);
		}

		protected override void OnRender(DrawingContext drawing_context) {
			var value = this.get_text(RenderSize.Width);
			if (!this.styled && this.highlight && this.theme_service is not null) {
				foreach (var span in this.snippet.spans) {
					if (span.start < 0 || span.start >= this.snippet.text.Length || span.length <= 0) continue;
					int length = Math.Min(span.length, this.snippet.text.Length - span.start);
					var color = this.theme_service.Theme.GetTextColor((ColorType)((uint)ColorType.Text + (uint)span.color));
					if (color.Foreground is not null) value.SetForegroundBrush(color.Foreground, span.start, length);
				}
				this.styled = true;
			}
			drawing_context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
			drawing_context.DrawText(value, new Point());
		}

		IMethod? reference_at(Point position) {
			if (!this.links_built) {
				var value = this.get_text(RenderSize.Width);
				foreach (var span in this.snippet.spans) {
					if (span.reference is not IMethod method || span.start < 0 || span.start >= this.snippet.text.Length || span.length <= 0) continue;
					var geometry = value.BuildHighlightGeometry(new Point(), span.start, Math.Min(span.length, this.snippet.text.Length - span.start));
					if (geometry is null) continue;
					geometry.Freeze();
					if (!this.link_regions.TryGetValue(method, out var regions)) this.link_regions[method] = regions = new List<Geometry>();
					regions.Add(geometry);
				}
				this.links_built = true;
			}
			return this.link_regions.FirstOrDefault(pair => pair.Value.Any(region => region.FillContains(position))).Key;
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
			if (e.ClickCount == 2 && this.reference_at(e.GetPosition(this)) is IMethod reference) {
				this.explore(reference); e.Handled = true;
			}
			base.OnMouseLeftButtonDown(e);
		}
		protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) {
			if (this.reference_at(e.GetPosition(this)) is IMethod reference) {
				var menu = this.create_menu(reference); menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true;
			}
			base.OnMouseRightButtonDown(e);
		}
	}
}
