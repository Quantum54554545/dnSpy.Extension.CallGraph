using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed record graph_layout_result(Dictionary<object, Rect> rectangles, Size extent, horizontal_graph_layout? horizontal = null);

	static class graph_layout {
		public const double minimum_automatic_width = 320;
		public const double maximum_automatic_width = 640;
		public static double automatic_width(string text, double maximum = graph_layout.maximum_automatic_width) {
			int longest = 0, column = 0;
			foreach (char character in text) {
				if (character == '\r') continue;
				if (character == '\n') { longest = Math.Max(longest, column); column = 0; }
				else column += character == '\t' ? 4 - column % 4 : 1;
				if (column * 6.95 + 22 >= maximum) return maximum;
			}
			return Math.Clamp(Math.Ceiling(Math.Max(longest, column) * 6.95 + 22), Math.Min(graph_layout.minimum_automatic_width, maximum), maximum);
		}
		public static double estimate_height(string text, double width, bool wrap) {
			int columns = Math.Max(1, (int)((width - 22) / 6.95));
			int rows = 0, line = 0;
			foreach (char character in text) {
				if (character == '\r') continue;
				if (character == '\n') { rows += wrap ? Math.Max(1, (line + columns - 1) / columns) : 1; line = 0; }
				else line += character == '\t' ? 4 : 1;
			}
			rows += wrap ? Math.Max(1, (line + columns - 1) / columns) : 1;
			return Math.Max(78, 60 + rows * 14.5);
		}
		public static graph_layout_result build(graph_presentation graph, IReadOnlyDictionary<object, highlighted_snippet> snippets,
			double width, bool wrap, bool vertical, CancellationToken token, bool automatic_width = false) {
			const double margin = 60, node_gap = 30, column_gap = 115;
			var groups = graph.nodes.GroupBy(node => node.layer).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
			var heights = new Dictionary<object, double>();
			var widths = new Dictionary<object, double>();
			foreach (var node in graph.nodes) {
				token.ThrowIfCancellationRequested();
				string text = snippets.TryGetValue(node.reference, out var snippet) ? snippet.text : string.Empty;
				widths[node.reference] = automatic_width ? graph_layout.automatic_width(text, width) : width;
				heights[node.reference] = graph_layout.estimate_height(text, widths[node.reference], wrap) + (node.reference is MethodDef && snippet?.is_truncated == true ? 25 : 0);
			}
			var rectangles = new Dictionary<object, Rect>();
			double maximum_height = groups.Select(group => group.Sum(node => heights[node.reference] + node_gap) - node_gap).DefaultIfEmpty(0).Max();
			double maximum_width = groups.Select(group => group.Sum(node => widths[node.reference] + node_gap) - node_gap).DefaultIfEmpty(0).Max();
			double next_y = margin, next_x = margin;
			for (int column = 0; column < groups.Length; column++) {
				token.ThrowIfCancellationRequested(); var group = groups[column];
				double group_height = group.Sum(node => heights[node.reference] + node_gap) - node_gap;
				double x = vertical ? margin + (maximum_width - (group.Sum(node => widths[node.reference] + node_gap) - node_gap)) / 2 : next_x;
				double y = vertical ? next_y : margin + (maximum_height - group_height) / 2;
				foreach (var node in group) {
					rectangles[node.reference] = new Rect(x, y, widths[node.reference], heights[node.reference]);
					if (vertical) x += widths[node.reference] + node_gap; else y += heights[node.reference] + node_gap;
				}
				if (vertical) next_y += group.Max(node => heights[node.reference]) + column_gap;
				else next_x += group.Max(node => widths[node.reference]) + column_gap;
			}
			var horizontal = vertical ? null : new horizontal_graph_layout(graph, token);
			if (horizontal is not null) rectangles = horizontal.compact(rectangles, token);
			return new graph_layout_result(rectangles, new Size(Math.Max(640, rectangles.Values.Select(rect => rect.Right + margin).DefaultIfEmpty(640).Max()),
				Math.Max(420, rectangles.Values.Select(rect => rect.Bottom + margin).DefaultIfEmpty(420).Max())), horizontal);
		}
		public static Dictionary<object, Rect> compact_horizontal(graph_presentation graph, IReadOnlyDictionary<object, Rect> sizes, CancellationToken token) {
			return new horizontal_graph_layout(graph, token).compact(sizes, token);
		}
		public static Dictionary<MethodDef, Point> map(call_graph_slice slice, CancellationToken token, IReadOnlyDictionary<MethodDef, Point>? retained = null) {
			token.ThrowIfCancellationRequested();
			var positions = new Dictionary<MethodDef, Point>();
			const double separation = 24;
			var occupied = new Dictionary<(long x, long y), List<Point>>();
			(long x, long y) cell(Point point) => ((long)Math.Floor(point.X / separation), (long)Math.Floor(point.Y / separation));
			void place(MethodDef method, Point point) {
				positions[method] = point; var key = cell(point);
				if (!occupied.TryGetValue(key, out var values)) occupied[key] = values = new List<Point>(); values.Add(point);
			}
			bool available(Point point) {
				var key = cell(point);
				for (long column = key.x - 1; column <= key.x + 1; column++) for (long row = key.y - 1; row <= key.y + 1; row++)
					if (occupied.TryGetValue((column, row), out var values)) foreach (var other in values) if ((other - point).LengthSquared < separation * separation) return false;
				return true;
			}
			foreach (var node in slice.nodes) {
				token.ThrowIfCancellationRequested();
				if (retained is not null && retained.TryGetValue(node.method, out var point) && double.IsFinite(point.X) && double.IsFinite(point.Y)) place(node.method, point);
				else if (node.is_root) place(node.method, new Point());
			}
			const double golden_angle = 2.399963229728653;
			foreach (var group in slice.nodes.GroupBy(node => node.layer).OrderBy(group => group.Key)) {
				int index = 0;
				double layer = Math.Abs((double)group.Key);
				double anchor = Math.Sign(group.Key) * (175 + (layer - 1) * 245);
				foreach (var node in group.OrderBy(node => node.method.MDToken.Raw)) {
					token.ThrowIfCancellationRequested();
					if (positions.ContainsKey(node.method)) continue;
					Point point;
					do {
						token.ThrowIfCancellationRequested();
						double angle = index * golden_angle + layer * 0.41;
						double radius = 28 * Math.Sqrt(index++ + 0.5);
						point = new Point(anchor + Math.Cos(angle) * radius * 0.78, Math.Sin(angle) * radius * 1.35);
					} while (Math.Abs(point.X - anchor) > 96 || !available(point));
					place(node.method, point);
				}
			}
			return positions;
		}
	}
}
