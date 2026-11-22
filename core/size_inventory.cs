using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed class size_inventory {
		readonly Dictionary<size_item, size_item> parents = new();
		public size_item root { get; }
		public IReadOnlyList<size_item> items { get; }
		public IReadOnlyList<size_item> methods { get; }
		public size_inventory(size_item root, CancellationToken token = default) {
			this.root = root; var all = new List<size_item>(); var pending = new Stack<size_item>(); pending.Push(root);
			while (pending.TryPop(out var item)) { token.ThrowIfCancellationRequested(); all.Add(item); foreach (var child in item.children) { this.parents[child] = item; pending.Push(child); } }
			this.items = all; this.methods = all.Where(item => item.method_references.Count != 0).OrderByDescending(item => item.bytes).ThenBy(item => item.name, StringComparer.Ordinal).ToArray();
		}
		public size_item? parent_of(size_item item) => this.parents.TryGetValue(item, out var parent) ? parent : item.parent;
		public IEnumerable<size_item> ancestors(size_item item) { while (this.parent_of(item) is size_item parent) { yield return parent; item = parent; } }
		public string path(size_item item) => string.Join(" / ", this.ancestors(item).Reverse().Select(parent => parent.name).Append(item.name));
		public static MethodDef? resolve(size_method_reference reference, IEnumerable<ModuleDef> modules) {
			foreach (var module in modules) {
				try {
					if (module.Mvid != reference.mvid || string.IsNullOrWhiteSpace(module.Location) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(module.Location), Path.GetFullPath(reference.path))) continue;
					if (module.ResolveToken((uint)reference.token) is MethodDef method && method.Name == reference.name && (uint)method.RVA == reference.rva) return method;
				} catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or BadImageFormatException or ObjectDisposedException) { }
			}
			return null;
		}
	}
}
