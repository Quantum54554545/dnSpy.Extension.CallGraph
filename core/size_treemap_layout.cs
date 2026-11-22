using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace dnSpy.CallGraph.Core {
	sealed record size_tile(size_item item, Rect rectangle, int depth);
	static class size_treemap_layout {
		public const int maximum_tiles = 8000;
		public static size_tile[] build(size_item root, Rect bounds, CancellationToken token = default) {
			token.ThrowIfCancellationRequested(); var result = new List<size_tile>();
			if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || root.bytes <= 0) return Array.Empty<size_tile>();
			if (root.children.Count == 0) return new[] { new size_tile(root, bounds, 0) };
			var children = root.children; int slots = Math.Clamp((int)Math.Min(bounds.Width * bounds.Height / 12, maximum_tiles), 256, maximum_tiles);
			if (children.Count > slots) children = children.Take(slots - 1).Append(size_item.collection(ui_text.pick("Other", "Остальные") + " · " + (children.Count - slots + 1), children.Skip(slots - 1), root)).ToArray();
			foreach (var tile in partition(children, bounds, token)) result.Add(new size_tile(tile.item, tile.rectangle, 1));
			return result.ToArray();
		}
		internal static IReadOnlyList<(size_item item, Rect rectangle)> partition(IReadOnlyList<size_item> items, Rect bounds, CancellationToken token) {
			var children = items.Where(item => item.bytes > 0).OrderByDescending(item => item.bytes).ToArray();
			var result = new List<(size_item, Rect)>(); double total = children.Sum(item => (double)item.bytes);
			if (total == 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
			var areas = children.Select(item => item.bytes / total * bounds.Width * bounds.Height).ToArray(); int start = 0;
			double worst(double sum, double largest, double smallest, double side) => Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * smallest));
			while (start < children.Length) {
				token.ThrowIfCancellationRequested(); double side = Math.Min(bounds.Width, bounds.Height); int end = start + 1; double sum = areas[start];
				while (end < children.Length && worst(sum + areas[end], areas[start], areas[end], side) <= worst(sum, areas[start], areas[end - 1], side)) sum += areas[end++];
				bool vertical = bounds.Width >= bounds.Height; double thickness = sum / side; double cursor = vertical ? bounds.Y : bounds.X;
				for (int position = start; position < end; position++) {
					token.ThrowIfCancellationRequested(); double extent = position == end - 1 ? (vertical ? bounds.Bottom : bounds.Right) - cursor : areas[position] / thickness;
					result.Add((children[position], vertical ? new Rect(bounds.X, cursor, Math.Max(0, thickness), Math.Max(0, extent)) : new Rect(cursor, bounds.Y, Math.Max(0, extent), Math.Max(0, thickness)))); cursor += extent;
				}
				bounds = vertical ? new Rect(bounds.X + thickness, bounds.Y, Math.Max(0, bounds.Width - thickness), bounds.Height) : new Rect(bounds.X, bounds.Y + thickness, bounds.Width, Math.Max(0, bounds.Height - thickness)); start = end;
			}
			return result;
		}
	}
}
