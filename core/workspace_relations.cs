using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	readonly record struct graph_selection_key(call_graph_index index, MethodDef root, call_graph_direction direction, int depth, int limit, string filter, bool structural, call_graph_slice? forward);
	sealed class graph_selection_cache {
		readonly Dictionary<graph_selection_key, (long revision, call_graph_slice slice, long weight)> entries = new();
		long weight;
		internal int count => this.entries.Count;
		internal long weight_value => this.weight;
		public bool try_get(graph_selection_key key, long revision, out call_graph_slice slice) {
			if (this.entries.TryGetValue(key, out var value) && value.revision == revision) { slice = value.slice; return true; }
			slice = null!; return false;
		}
		public void store(graph_selection_key key, long revision, call_graph_slice slice) {
			if (this.entries.Remove(key, out var old)) this.weight -= old.weight;
			long cost = slice.nodes.Count + slice.call_sites.Sum(site => 1L + site.targets.Count + site.evidence.Count) + slice.links.Sum(link => (long)link.evidence.Count);
			if (cost > 200000) return;
			while (this.entries.Count != 0 && (this.entries.Count >= 32 || this.weight + cost > 200000)) {
				var first = this.entries.First(); this.entries.Remove(first.Key); this.weight -= first.Value.weight;
			}
			this.entries[key] = (revision, slice, cost); this.weight += cost;
		}
	}
	sealed partial class workspace_analysis {
		internal readonly graph_selection_cache selections = new();
		readonly Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> discoveries = new();
		readonly Dictionary<(MethodDef, int, int, string), long> query_revisions = new();
		readonly Dictionary<call_graph_index, (long revision, call_graph_index index)> discovery_indexes = new();
		ILookup<MethodDef, call_graph_link>? discovery_outgoing;
		call_graph_link[]? discovery_links;
		long discovery_revision;
		internal long relation_revision => this.discovery_revision;
		Dictionary<MethodDef, MethodDef[]>? address_owners;
		Dictionary<FieldDef, MethodDef[]>? field_readers;
		Dictionary<string, List<(MethodDef method, IField reference)>>? field_read_candidates;
		readonly Dictionary<(object scope, MethodDef method), call_graph_link[]> virtual_incoming = new();
		readonly Dictionary<TypeDef, string[]> incoming_aliases = new();
		Dictionary<string, MethodDef[]>? reflection_owners;
		readonly Dictionary<MethodDef, MethodDef[]> incoming_seeds = new();
		readonly Dictionary<(call_graph_index index, MethodDef root), (bool pending, int next)> incoming_attempts = new();
		internal void record_discoveries(IEnumerable<call_graph_link> links, bool contextual = false) {
			bool changed = false;
			foreach (var link in links) {
				if (link.evidence.All(item => item.kind == call_evidence_kind.il_operand)) continue;
				var key = (link.source, link.target);
				if (!this.discoveries.TryGetValue(key, out var accumulator)) this.discoveries[key] = accumulator = new call_site_accumulator();
				int evidence_count = accumulator.evidence.Count, offset_count = accumulator.offsets.Count; bool direct = accumulator.has_direct;
				foreach (var item in link.evidence.Where(item => item.kind != call_evidence_kind.il_operand)) accumulator.add(item.offset, item.operation, contextual || item.is_potential,
					contextual && !item.is_contextual ? "Target resolved in an analyzed caller context; " + item.reason : item.reason, contextual ? call_evidence_kind.possible_target : item.classification, contextual || item.is_contextual);
				changed |= evidence_count != accumulator.evidence.Count || offset_count != accumulator.offsets.Count || direct != accumulator.has_direct;
			}
			if (changed) { this.discovery_revision++; this.discovery_outgoing = null; this.discovery_links = null; this.discovery_indexes.Clear(); }
		}
		call_graph_link[] all_discoveries => this.discovery_links ??= this.discoveries.Select(pair => workspace_analysis.link(pair.Key.source, pair.Key.target, pair.Value)).ToArray();
		IEnumerable<call_graph_link> discovered_outgoing(MethodDef method) {
			this.discovery_outgoing ??= this.all_discoveries.ToLookup(link => link.source);
			return this.discovery_outgoing[method];
		}
		internal call_graph_index include_discoveries(call_graph_index index, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			if (this.discoveries.Count == 0) return index;
			if (this.discovery_indexes.TryGetValue(index, out var cached) && cached.revision == this.discovery_revision) return cached.index;
			var result = index.merge_links(this.all_discoveries, token);
			if (this.discovery_indexes.Count >= 8) this.discovery_indexes.Clear();
			this.discovery_indexes[index] = (this.discovery_revision, result);
			this.discovery_indexes[result] = (this.discovery_revision, result);
			return result;
		}
		internal IReadOnlyList<call_graph_link> incoming_links(call_graph_index index, MethodDef method, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			if (method.IsStatic) return index.incoming_value(method);
			var key = ((object)index.methods, method);
			if (!this.virtual_incoming.TryGetValue(key, out var links)) {
				var names = new[] { method.Name.ToString() }.Concat(this.alias_names(method.DeclaringType, token));
				var sites = index.virtual_sites_named(names).GroupBy(site => site.declaration);
				var resolved = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
				foreach (var group in sites) {
					token.ThrowIfCancellationRequested();
					if (!method.IsVirtual && method.Overrides.Count == 0 && !group.Key.DeclaringType.IsInterface) continue;
					if (!this.resolver_value.virtual_targets_value(group.Key).Contains(method)) continue;
					foreach (var site in group) call_graph_index.add_call_site(resolved, site.source, method, site.offset, site.operation, true, "possible virtual target in the loaded workspace", call_evidence_kind.possible_target);
				}
				links = resolved.Select(pair => workspace_analysis.link(pair.Key.source, pair.Key.target, pair.Value)).ToArray();
				if (this.virtual_incoming.Count >= 128) this.virtual_incoming.Remove(this.virtual_incoming.Keys.First());
				this.virtual_incoming[key] = links; this.record_discoveries(links);
			}
			return links.Length == 0 ? index.incoming_value(method) : index.incoming_value(method).Concat(links).GroupBy(link => (link.source, link.target))
				.Select(group => { var accumulator = new call_site_accumulator(); foreach (var link in group) accumulator.add(link); return workspace_analysis.link(group.Key.source, group.Key.target, accumulator); }).ToArray();
		}
		IEnumerable<string> alias_names(TypeDef type, CancellationToken token) {
			if (this.incoming_aliases.TryGetValue(type, out var cached)) return cached;
			var names = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<TypeDef>(); var pending = new Queue<TypeDef>(); pending.Enqueue(type);
			while (pending.Count != 0) {
				token.ThrowIfCancellationRequested(); var current = pending.Dequeue(); if (!visited.Add(current)) continue;
				foreach (var method in current.Methods) foreach (var item in method.Overrides) names.Add(item.MethodDeclaration.Name.ToString());
				ITypeDefOrRef? parent = current.BaseType;
				if (parent is TypeSpec specification && specification.TypeSig is GenericInstSig instance) parent = instance.GenericType.TypeDefOrRef;
				if (parent is not null && this.types_value.TryGetValue(call_graph_index.safe_type_name(parent), out var candidates))
					foreach (var candidate in candidates) if (parent.DefinitionAssembly is null || StringComparer.OrdinalIgnoreCase.Equals(parent.DefinitionAssembly.FullName, candidate.DefinitionAssembly?.FullName)) pending.Enqueue(candidate);
			}
			if (this.incoming_aliases.Count >= 256) this.incoming_aliases.Remove(this.incoming_aliases.Keys.First());
			return this.incoming_aliases[type] = names.ToArray();
		}
		IEnumerable<MethodDef> readers_of(FieldDef field, CancellationToken token) {
			if (this.field_readers!.TryGetValue(field, out var cached)) return cached;
			var result = new HashSet<MethodDef>();
			foreach (var candidate in this.field_read_candidates!.GetValueOrDefault(field.Name.ToString()) ?? new List<(MethodDef method, IField reference)>()) {
				token.ThrowIfCancellationRequested();
				try { if (candidate.reference.ResolveFieldDef() == field) result.Add(candidate.method); } catch (Exception) { }
			}
			return this.field_readers[field] = result.ToArray();
		}
		internal call_graph_index refine_callers(call_graph_index index, MethodDef root, CancellationToken token, bool resume_refinement = false) {
			token.ThrowIfCancellationRequested();
			var clock = Stopwatch.StartNew();
			var query = (index, root);
			bool attempted = this.incoming_attempts.TryGetValue(query, out var previous);
			if (!resume_refinement && attempted) {
				var cached = this.refine_index(index, token, resume_refinement: false);
				return previous.pending ? cached.with_pending_callers(root) : cached;
			}
			if (this.address_owners is null) {
				var owners = new Dictionary<MethodDef, HashSet<MethodDef>>(); var readers = new Dictionary<string, List<(MethodDef method, IField reference)>>(StringComparer.Ordinal); var reflection = new Dictionary<string, HashSet<MethodDef>>(StringComparer.Ordinal);
				foreach (var method in index.methods) {
					token.ThrowIfCancellationRequested(); var structure = this.call_structure(method, token);
					foreach (var instruction in structure.address_references) {
						if (instruction.Operand is not IMethod reference || this.resolve(reference) is not MethodDef target) continue;
						if (!owners.TryGetValue(target, out var sources)) owners[target] = sources = new HashSet<MethodDef>();
						sources.Add(method);
					}
					bool lookup = structure.calls.Any(call => call.Operand is IMethod reference && reference.Name.ToString() is "GetMethod" or "GetMethods" or "GetMember" or "GetMembers" or "GetMethodFromHandle");
					if (method.HasBody) foreach (var instruction in method.Body.Instructions) {
						if (instruction.OpCode.Code is dnlib.DotNet.Emit.Code.Ldfld or dnlib.DotNet.Emit.Code.Ldsfld or dnlib.DotNet.Emit.Code.Ldflda or dnlib.DotNet.Emit.Code.Ldsflda && instruction.Operand is IField field) {
							string field_name = field.Name.ToString(); if (!readers.TryGetValue(field_name, out var sources)) readers[field_name] = sources = new(); sources.Add((method, field));
						}
						if (!lookup) continue;
						if (instruction.OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr && instruction.Operand is string name) {
							if (!reflection.TryGetValue(name, out var sources)) reflection[name] = sources = new HashSet<MethodDef>(); sources.Add(method);
						}
						if (instruction.OpCode.Code == dnlib.DotNet.Emit.Code.Ldtoken && instruction.Operand is IMethod reference && this.resolve(reference) is MethodDef target) {
							if (!owners.TryGetValue(target, out var sources)) owners[target] = sources = new HashSet<MethodDef>(); sources.Add(method);
						}
					}
				}
				this.address_owners = owners.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
				this.field_readers = new(); this.field_read_candidates = readers;
				this.reflection_owners = reflection.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
			}
			if (!this.incoming_seeds.TryGetValue(root, out var seeds)) {
				var found = new HashSet<MethodDef>(this.address_owners.GetValueOrDefault(root) ?? Array.Empty<MethodDef>());
				found.UnionWith(this.reflection_owners!.GetValueOrDefault(root.Name.ToString()) ?? Array.Empty<MethodDef>());
				foreach (var owner in found.ToArray()) foreach (var field in this.structure(owner, token).writes) found.UnionWith(this.readers_of(field, token));
				var pending = new Queue<(MethodDef method, int depth)>(found.Select(method => (method, 0)));
				while (pending.Count != 0) {
					token.ThrowIfCancellationRequested(); var current = pending.Dequeue(); if (current.depth >= 8) continue;
					foreach (var link in index.incoming_value(current.method)) if (found.Add(link.source)) pending.Enqueue((link.source, current.depth + 1));
				}
				seeds = found.OrderBy(method => this.call_structure(method, token).calls.Any(call => call.OpCode.Code == dnlib.DotNet.Emit.Code.Calli) ? 0 : 1).ThenBy(call_graph_index.safe_token).ToArray();
				if (this.incoming_seeds.Count >= 64) this.incoming_seeds.Remove(this.incoming_seeds.Keys.First());
				this.incoming_seeds[root] = seeds;
			}
			bool pending_seeds = false; int next = seeds.Length == 0 ? 0 : previous.next % seeds.Length;
			for (int visited = 0; visited < seeds.Length; visited++) {
				token.ThrowIfCancellationRequested();
				var remaining = TimeSpan.FromMilliseconds(resume_refinement ? 1000 : 400) - clock.Elapsed;
				if (remaining <= TimeSpan.Zero) { pending_seeds = true; break; }
				var seed = seeds[next]; next = (next + 1) % seeds.Length;
				var result = this.browse(seed, 120, call_graph_filter_options.none, token, depth: 8, resume_refinement: resume_refinement, budget_override: new refinement_budget(remaining, 32L * 1024 * 1024));
				pending_seeds |= result.slice.can_continue_refinement;
			}
			if (this.incoming_attempts.Count >= 64 && !attempted) this.incoming_attempts.Remove(this.incoming_attempts.Keys.First());
			var refined = this.refine_index(index, token, resume_refinement: resume_refinement, budget_override: new refinement_budget(TimeSpan.FromMilliseconds(resume_refinement ? 1000 : 200), 64L * 1024 * 1024));
			this.incoming_attempts[query] = (pending_seeds, next);
			return pending_seeds ? refined.with_pending_callers(root) : refined;
		}
	}
}
