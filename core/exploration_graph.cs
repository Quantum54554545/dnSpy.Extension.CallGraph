using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed class exploration_graph {
		readonly Dictionary<MethodDef, call_graph_node> nodes = new();
		readonly Dictionary<(MethodDef, MethodDef), call_graph_link> links = new();
		readonly Dictionary<(MethodDef, uint), call_site_info> sites = new();
		readonly Dictionary<MethodDef, graph_diagnostic[]> notes = new();
		readonly HashSet<MethodDef> opened = new();
		readonly HashSet<MethodDef> collapsed = new();
		readonly HashSet<MethodDef> pinned = new();
		readonly Dictionary<MethodDef, bool> pending_refinement = new();
		HashSet<MethodDef> previous = new();
		readonly call_graph_filter_options filter;
		ILookup<MethodDef, call_graph_link>? outgoing;
		ILookup<MethodDef, call_graph_link>? incoming;
		public MethodDef root { get; }
		public call_graph_direction direction { get; }
		public int limit { get; }
		public int known_method_count => this.nodes.Count;
		public bool can_continue_refinement { get; private set; }
		public exploration_graph copy() {
			var copy = new exploration_graph(this.root, this.direction, this.limit, this.filter) { can_continue_refinement = this.can_continue_refinement, previous = new HashSet<MethodDef>(this.previous) };
			foreach (var pair in this.nodes) copy.nodes.Add(pair.Key, pair.Value);
			foreach (var pair in this.links) copy.links.Add(pair.Key, pair.Value);
			foreach (var pair in this.sites) copy.sites.Add(pair.Key, pair.Value);
			foreach (var pair in this.notes) copy.notes.Add(pair.Key, pair.Value);
			foreach (var pair in this.pending_refinement) copy.pending_refinement.Add(pair.Key, pair.Value);
			copy.opened.UnionWith(this.opened); copy.collapsed.UnionWith(this.collapsed); copy.pinned.UnionWith(this.pinned); return copy;
		}
		public exploration_graph(MethodDef root, call_graph_direction direction, int limit, call_graph_filter_options? filter = null) {
			this.root = root; this.direction = direction; this.limit = Math.Max(2, limit); filter ??= call_graph_filter_options.none;
			this.filter = new call_graph_filter_options(filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors, filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count);
		}
		public bool is_pinned(MethodDef method) => this.pinned.Contains(method);
		public bool pin(MethodDef method) { if (this.pinned.Remove(method)) return true; if (method == this.root || this.pinned.Count >= this.limit - 1) return false; this.pinned.Add(method); return true; }
		public bool can_pin_path(IEnumerable<MethodDef> methods) => this.pinned.Concat(methods).Append(this.root).Distinct().Count() <= this.limit;
		public void collapse(MethodDef method) => this.collapsed.Add(method);
		public void expand(MethodDef method) { this.opened.Add(method); this.collapsed.Remove(method); }
		public void merge(call_graph_slice slice, bool initial = false) {
			this.outgoing = this.incoming = null;
			var query_root = slice.nodes.FirstOrDefault(node => node.is_root)?.method;
			if (query_root is not null && (!slice.is_structural || slice.can_continue_refinement)) this.pending_refinement[query_root] = slice.can_continue_refinement;
			this.can_continue_refinement = this.pending_refinement.Values.Any(value => value);
			foreach (var node in slice.nodes) {
				this.nodes[node.method] = node;
				if (initial && node.is_expanded) this.opened.Add(node.method);
			}
			foreach (var link in slice.links) {
				var key = (source: link.source, target: link.target);
				var evidence = this.links.TryGetValue(key, out var existing) ? existing.evidence.Concat(link.evidence).Distinct().ToArray() : link.evidence.ToArray();
				this.links[key] = new call_graph_link(link.source, link.target, evidence.Select(item => item.offset).Distinct().Count(), evidence.Select(item => item.offset).Distinct().Order().ToArray(), evidence.Select(item => item.operation).Distinct().ToArray(), evidence.All(item => item.is_potential), evidence);
			}
			foreach (var incoming_site in slice.call_sites) {
				var site = incoming_site;
				var key = (source: site.source, offset: site.offset);
				if (this.sites.TryGetValue(key, out var existing)) site = site with { targets = existing.targets.Concat(site.targets).Distinct().ToArray(), evidence = existing.evidence.Concat(site.evidence).Distinct().ToArray() };
				this.sites[key] = site;
				foreach (var target in site.targets) {
					this.nodes.TryAdd(target, new call_graph_node(target, 1, false, !ReferenceEquals(target.Module, this.root.Module), ui_text.pick("Expand IL relations here", "Раскрыть IL-связи здесь"), false));
					var target_evidence = site.evidence.Where(item => item.target == target).ToArray();
					if (target_evidence.Length == 0) continue;
					var link_key = (source: site.source, target: target);
					var all = (this.links.TryGetValue(link_key, out var current) ? current.evidence : Array.Empty<call_site_evidence>()).Concat(target_evidence).Distinct().ToArray();
					this.links[link_key] = new call_graph_link(site.source, target, all.Select(item => item.offset).Distinct().Count(), all.Select(item => item.offset).Distinct().ToArray(), all.Select(item => item.operation).Distinct().ToArray(), all.All(item => item.is_potential), all);
				}
			}
			foreach (var node in slice.nodes.Where(node => node.is_expanded)) this.notes[node.method] = slice.is_structural && this.notes.TryGetValue(node.method, out var previous_notes) ? previous_notes.Concat(slice.diagnostics_for(node.method)).Distinct().ToArray() : slice.diagnostics_for(node.method).ToArray();
			foreach (var group in slice.diagnostics.GroupBy(note => note.method)) if (!this.notes.ContainsKey(group.Key)) this.notes[group.Key] = group.ToArray();
			foreach (var note in slice.diagnostics.Where(note => note.kind == graph_diagnostic_kind.address_reference && note.target is not null)) this.nodes.TryAdd(note.target!, new call_graph_node(note.target!, 1, false, !ReferenceEquals(note.target!.Module, this.root.Module), ui_text.pick("Address reference — expand here", "Ссылка на адрес — раскрыть здесь"), false));
		}
		IEnumerable<(MethodDef method, bool incoming)> neighbors(MethodDef method) {
			if (this.direction != call_graph_direction.callers) {
				foreach (var link in this.outgoing![method].Where(link => this.filter.includes(this.root, link, link.target))) yield return (method: link.target, false);
				if (this.notes.TryGetValue(method, out var values)) foreach (var note in values.Where(note => note.kind == graph_diagnostic_kind.address_reference && note.target is not null && this.filter.includes(this.root, note.target))) yield return (note.target!, false);
			}
			if (this.direction != call_graph_direction.callees) foreach (var link in this.incoming![method].Where(link => this.filter.includes(this.root, link, link.source))) yield return (method: link.source, true);
		}
		public call_graph_slice project(Func<MethodDef, object>? card_key = null) {
			this.outgoing ??= this.links.Values.ToLookup(link => link.source); this.incoming ??= this.links.Values.ToLookup(link => link.target);
			card_key ??= method => method;
			var cards = new HashSet<object> { this.root };
			var layers = new Dictionary<MethodDef, int> { [this.root] = 0 };
			foreach (var method in this.pinned.Where(this.nodes.ContainsKey)) { var key = card_key(method); if (cards.Count >= this.limit && !cards.Contains(key)) continue; cards.Add(key); layers.TryAdd(method, this.nodes[method].layer); }
			var pending = new Queue<MethodDef>(layers.Keys);
			while (pending.Count != 0) {
				var method = pending.Dequeue();
				if (!this.opened.Contains(method) || this.collapsed.Contains(method)) continue;
				foreach (var next in this.neighbors(method).Distinct().OrderBy(item => this.previous.Contains(item.method) ? 0 : 1).ThenBy(item => call_graph_index.display_name(item.method), StringComparer.Ordinal)) {
					if (layers.ContainsKey(next.method) || !this.nodes.ContainsKey(next.method)) continue;
					var key = next.method == this.root ? this.root : card_key(next.method); if (cards.Count >= this.limit && !cards.Contains(key)) continue; cards.Add(key);
					layers.Add(next.method, layers[method] + (next.incoming ? -1 : 1)); pending.Enqueue(next.method);
				}
			}
			var visible = layers.Keys.ToHashSet();
			var diagnostics = new List<graph_diagnostic>();
			var projected = new List<call_graph_node>();
			foreach (var pair in layers) {
				var method = pair.Key; var node = this.nodes.GetValueOrDefault(method) ?? new call_graph_node(method, pair.Value, method == this.root, false);
				var original = this.notes.GetValueOrDefault(method) ?? Array.Empty<graph_diagnostic>();
				diagnostics.AddRange(original.Where(note => note.kind is not (graph_diagnostic_kind.node_limit or graph_diagnostic_kind.view_depth)));
				int remaining = this.neighbors(method).Select(item => item.method).Distinct().Count(target => !visible.Contains(target));
				bool is_open = this.opened.Contains(method) && !this.collapsed.Contains(method);
				string hint = string.Empty;
				if (this.collapsed.Contains(method)) hint = ui_text.pick("Branch collapsed — expand here", "Ветка свёрнута — раскрыть здесь");
				else if (remaining != 0) hint = ui_text.pick($"Targets outside view: {remaining} — expand here; collapse or group branches if the view is full", $"Целей вне фрагмента: {remaining} — раскрыть здесь; если он заполнен, сверни ветви или объедини методы в группу");
				else if (!is_open) hint = node.expansion_hint.Replace("исследовать отсюда", "раскрыть здесь").Replace("Explore from here", "expand here");
				else if (original.Any(note => note.kind == graph_diagnostic_kind.node_limit)) hint = ui_text.pick("More relations available — expand here", "Есть продолжение — раскрыть здесь");
				if (hint.Length != 0) diagnostics.Add(new graph_diagnostic(method, 0, remaining != 0 ? graph_diagnostic_kind.node_limit : graph_diagnostic_kind.view_depth, hint));
				projected.Add(new call_graph_node(method, pair.Value, method == this.root, node.is_external, hint, is_open));
			}
			var displayed_links = this.links.Values.Where(link => link.call_site_count >= this.filter.minimum_call_site_count && visible.Contains(link.source) && visible.Contains(link.target) &&
				(this.direction == call_graph_direction.callers ? !this.collapsed.Contains(link.target) : this.direction == call_graph_direction.callees ? !this.collapsed.Contains(link.source) : !this.collapsed.Contains(link.source) || !this.collapsed.Contains(link.target))).ToArray();
			this.previous = visible;
			var displayed_sites = this.sites.Values.Where(site => visible.Contains(site.source)).ToArray();
			return new call_graph_slice(projected, displayed_links, diagnostics.Any(note => note.kind is graph_diagnostic_kind.node_limit or graph_diagnostic_kind.view_depth), displayed_sites.Count(site => site.operation == "calli" && site.has_unknown_targets), diagnostics, displayed_sites, false, this.can_continue_refinement, this.direction);
		}
	}
}
