using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record method_effects(FieldDef[] reads, FieldDef[] writes, bool has_unknown_effects, bool is_recursive, ModuleDef[] dependencies);

	sealed class method_summary_index {
		sealed class effects {
			public readonly HashSet<FieldDef> reads = new HashSet<FieldDef>();
			public readonly HashSet<FieldDef> writes = new HashSet<FieldDef>();
			public readonly HashSet<ModuleDef> dependencies = new HashSet<ModuleDef>();
			public bool unknown;
			public bool recursive;
			public bool merge(effects other) {
				int reads = this.reads.Count, writes = this.writes.Count, dependencies = this.dependencies.Count; bool previous = this.unknown;
				this.reads.UnionWith(other.reads); this.writes.UnionWith(other.writes); this.dependencies.UnionWith(other.dependencies); this.unknown |= other.unknown;
				if (this.reads.Count + this.writes.Count > 4096) { this.reads.Clear(); this.writes.Clear(); this.unknown = true; }
				return reads != this.reads.Count || writes != this.writes.Count || dependencies != this.dependencies.Count || previous != this.unknown;
			}
		}
		readonly workspace_analysis workspace;
		readonly ConcurrentDictionary<MethodDef, method_effects> summaries;
		public int component_count { get; private set; }
		public int fixed_point_passes { get; private set; }
		public method_summary_index(workspace_analysis workspace, ConcurrentDictionary<MethodDef, method_effects>? summaries = null) {
			this.workspace = workspace; this.summaries = summaries ?? new ConcurrentDictionary<MethodDef, method_effects>();
		}
		public method_effects get(MethodDef root, CancellationToken token) {
			if (this.summaries.TryGetValue(root, out var cached)) return cached;
			var graph = new Dictionary<MethodDef, MethodDef[]>();
			var effects = new Dictionary<MethodDef, global::dnSpy.CallGraph.Core.method_summary_index.effects>();
			var pending = new Queue<MethodDef>(); pending.Enqueue(root); var discovered = new HashSet<MethodDef> { root };
			while (pending.Count != 0) {
				token.ThrowIfCancellationRequested();
				var method = pending.Dequeue(); if (graph.ContainsKey(method)) continue;
				var current = new global::dnSpy.CallGraph.Core.method_summary_index.effects(); effects[method] = current; current.dependencies.Add(method.Module);
				if (this.summaries.TryGetValue(method, out var known)) {
					current.reads.UnionWith(known.reads); current.writes.UnionWith(known.writes); current.dependencies.UnionWith(known.dependencies);
					current.unknown = known.has_unknown_effects; current.recursive = known.is_recursive; graph[method] = Array.Empty<MethodDef>(); continue;
				}
				if (graph.Count >= 4096 || !this.workspace.modules.Contains(method.Module) || !method.HasBody) {
					current.unknown = true; graph[method] = Array.Empty<MethodDef>(); continue;
				}
				var structure = this.workspace.structure(method, token);
				current.reads.UnionWith(structure.reads); current.writes.UnionWith(structure.writes); current.unknown = structure.has_unknown_effects;
				current.dependencies.UnionWith(structure.reads.Concat(structure.writes).Select(field => field.Module));
				var callees = new HashSet<MethodDef>();
				foreach (var instruction in structure.calls) {
					if (instruction.Operand is not IMethod reference || this.workspace.resolve(reference) is not MethodDef target) { current.unknown = true; continue; }
					if (!discovered.Contains(target) && discovered.Count >= 4096) { current.unknown = true; continue; }
					callees.Add(target);
					if (instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) current.unknown = true;
				}
				graph[method] = callees.ToArray();
				foreach (var callee in callees) if (discovered.Add(callee)) pending.Enqueue(callee);
			}
			var visited = new HashSet<MethodDef>(); var finished = new List<MethodDef>();
			foreach (var seed in graph.Keys) {
				var stack = new Stack<(MethodDef method, bool exit)>(); stack.Push((method: seed, false));
				while (stack.Count != 0) {
					token.ThrowIfCancellationRequested(); var entry = stack.Pop();
					if (entry.exit) { finished.Add(entry.method); continue; }
					if (!visited.Add(entry.method)) continue;
					stack.Push((method: entry.method, true)); foreach (var callee in graph[entry.method]) if (!visited.Contains(callee)) stack.Push((method: callee, false));
				}
			}
			var reverse = graph.Keys.ToDictionary(method => method, _ => new List<MethodDef>());
			foreach (var pair in graph) foreach (var callee in pair.Value) reverse[callee].Add(pair.Key);
			visited.Clear(); var components = new List<MethodDef[]>();
			foreach (var seed in finished.AsEnumerable().Reverse()) {
				if (!visited.Add(seed)) continue;
				var members = new List<MethodDef>(); var stack = new Stack<MethodDef>(); stack.Push(seed);
				while (stack.Count != 0) { var method = stack.Pop(); members.Add(method); foreach (var caller in reverse[method]) if (visited.Add(caller)) stack.Push(caller); }
				components.Add(members.ToArray());
			}
			foreach (var component in components.AsEnumerable().Reverse()) {
				bool recursive = component.Length > 1 || graph[component[0]].Contains(component[0]);
				foreach (var method in component) effects[method].recursive |= recursive;
				bool changed;
				int passes = 0;
				do {
					token.ThrowIfCancellationRequested(); changed = false;
					foreach (var method in component) foreach (var callee in graph[method]) changed |= effects[method].merge(effects[callee]);
					this.fixed_point_passes++;
					if (++passes > Math.Min(4096, component.Length + 1)) {
						foreach (var method in component) effects[method].unknown = true;
						break;
					}
				} while (changed);
				foreach (var method in component) {
					var value = effects[method];
					this.summaries[method] = new method_effects(value.reads.ToArray(), value.writes.ToArray(), value.unknown, value.recursive, value.dependencies.ToArray());
				}
			}
			this.component_count += components.Count;
			return this.summaries[root];
		}
	}
}
