using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace dnSpy.CallGraph.Core {
	sealed class horizontal_graph_layout {
		readonly object[] references;
		readonly int[][] layers;
		readonly int[][] neighbors;
		public horizontal_graph_layout(graph_presentation graph, CancellationToken token) {
			this.references = graph.nodes.Select(node => node.reference).ToArray();
			var indices = new Dictionary<object, int>(this.references.Length);
			for (int index = 0; index < this.references.Length; index++) indices.Add(this.references[index], index);
			this.layers = Enumerable.Range(0, this.references.Length).GroupBy(index => graph.nodes[index].layer).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
			var adjacent = new HashSet<int>[this.references.Length];
			for (int index = 0; index < adjacent.Length; index++) adjacent[index] = new HashSet<int>();
			foreach (var link in graph.links) {
				token.ThrowIfCancellationRequested();
				if (!indices.TryGetValue(link.source, out int source) || !indices.TryGetValue(link.target, out int target) || graph.nodes[source].layer == graph.nodes[target].layer) continue;
				adjacent[source].Add(target); adjacent[target].Add(source);
			}
			this.neighbors = adjacent.Select(values => values.ToArray()).ToArray();
		}
		public Dictionary<object, Rect> compact(IReadOnlyDictionary<object, Rect> sizes, CancellationToken token) {
			var rectangles = new Rect[this.references.Length];
			var desired = new double[this.references.Length];
			var scratch = new double[Math.Max(this.layers.Select(layer => layer.Length).DefaultIfEmpty().Max(), this.neighbors.Select(values => values.Length).DefaultIfEmpty().Max())];
			var order = new int[this.layers.Select(layer => layer.Length).DefaultIfEmpty().Max()];
			for (int index = 0; index < this.references.Length; index++) rectangles[index] = sizes[this.references[index]];
			foreach (var layer in this.layers) {
				double top = 60;
				foreach (int index in layer) { rectangles[index].Y = top; top += rectangles[index].Height + 30; }
			}
			var comparer = Comparer<int>.Create((left, right) => { int comparison = desired[left].CompareTo(desired[right]); return comparison == 0 ? left.CompareTo(right) : comparison; });
			for (int sweep = 0; sweep < 4; sweep++) {
				for (int column = 0; column < this.layers.Length; column++) {
					token.ThrowIfCancellationRequested();
					var layer = this.layers[sweep % 2 == 0 ? column : this.layers.Length - column - 1];
					foreach (int index in layer) {
						var adjacent = this.neighbors[index];
						for (int neighbor = 0; neighbor < adjacent.Length; neighbor++) { var rect = rectangles[adjacent[neighbor]]; scratch[neighbor] = rect.Top + rect.Height / 2; }
						desired[index] = adjacent.Length == 0 ? rectangles[index].Top + rectangles[index].Height / 2 : horizontal_graph_layout.median(scratch, adjacent.Length);
					}
					Array.Copy(layer, order, layer.Length); Array.Sort(order, 0, layer.Length, comparer);
					double cursor = 0;
					for (int position = 0; position < layer.Length; position++) { int index = order[position]; scratch[position] = desired[index] - cursor - rectangles[index].Height / 2; cursor += rectangles[index].Height + 30; }
					cursor = Math.Max(60, horizontal_graph_layout.median(scratch, layer.Length));
					for (int position = 0; position < layer.Length; position++) { int index = order[position]; rectangles[index].Y = cursor; cursor += rectangles[index].Height + 30; }
				}
			}
			double minimum = 60;
			if (rectangles.Length != 0) { minimum = double.MaxValue; foreach (var rect in rectangles) minimum = Math.Min(minimum, rect.Top); }
			var result = new Dictionary<object, Rect>(this.references.Length);
			for (int index = 0; index < this.references.Length; index++) { var rect = rectangles[index]; rect.Y -= minimum - 60; result.Add(this.references[index], rect); }
			return result;
		}
		static double median(double[] values, int count) {
			if (count == 0) return 60;
			Array.Sort(values, 0, count);
			return count % 2 == 0 ? (values[count / 2 - 1] + values[count / 2]) / 2 : values[count / 2];
		}
	}
}
