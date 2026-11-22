using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record method_structure(Instruction[] calls, FieldDef[] reads, FieldDef[] writes, bool has_unknown_effects, bool is_incomplete, Instruction[] address_references);

	sealed partial class workspace_analysis {
		readonly ConcurrentDictionary<MethodDef, method_structure> structures;
		readonly ConcurrentDictionary<MethodDef, method_structure> call_structures;
		readonly ConcurrentDictionary<MethodDef, method_effects> effects;
		readonly Dictionary<IMethod, MethodDef?> resolved = new Dictionary<IMethod, MethodDef?>();
		readonly HashSet<IMethod> resolution_attempted = new HashSet<IMethod>();
		readonly Dictionary<(MethodDef root, int depth, int limit, string filter), (call_graph_index index, call_graph_slice slice)> queries = new Dictionary<(MethodDef, int, int, string), (call_graph_index, call_graph_slice)>();
		readonly Dictionary<(MethodDef root, int depth, int limit, string filter), reachable_call_analysis> sessions = new();
		Dictionary<string, TypeDef[]>? types;
		workspace_call_resolver? resolver;
		method_summary_index? summaries;
		readonly Dictionary<(call_graph_index index, bool preserve, bool thorough), call_graph_index> refined_indexes = new();
		readonly Dictionary<(call_graph_index index, bool preserve, bool thorough), reachable_call_analysis> index_sessions = new();
		public HashSet<ModuleDef> modules { get; }
		public Dictionary<string, TypeDef[]> types_value => this.types ??= this.modules.SelectMany(module => module.GetTypes())
			.GroupBy(type => type.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
		public workspace_call_resolver resolver_value => this.resolver ??= new workspace_call_resolver(new HashSet<MethodDef>(this.types_value.Values.SelectMany(values => values).SelectMany(type => type.Methods)));
		public method_summary_index summaries_value => this.summaries ??= new method_summary_index(this, this.effects);
		public readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
		public int scanned_method_count { get; private set; }
		public int analysis_count { get; private set; }
		internal Func<refinement_budget>? create_refinement_budget { get; set; }

		public workspace_analysis(IEnumerable<ModuleDef> modules, ConcurrentDictionary<MethodDef, method_structure>? structures = null,
			ConcurrentDictionary<MethodDef, method_effects>? effects = null, ConcurrentDictionary<MethodDef, method_structure>? call_structures = null) {
			this.modules = new HashSet<ModuleDef>(modules);
			this.structures = structures ?? new ConcurrentDictionary<MethodDef, method_structure>();
			this.effects = effects ?? new ConcurrentDictionary<MethodDef, method_effects>();
			this.call_structures = call_structures ?? new();
		}
		public call_graph_index refine_index(call_graph_index index, CancellationToken token, bool preserve_structural_calls = true, bool thorough_refinement = false, bool resume_refinement = true, refinement_budget? budget_override = null) {
			token.ThrowIfCancellationRequested();
			var key = (index: index, preserve_structural_calls: preserve_structural_calls, thorough_refinement: thorough_refinement);
			if (this.refined_indexes.TryGetValue(key, out var cached) && (!resume_refinement || !this.index_sessions.ContainsKey(key))) return this.include_discoveries(cached, token);
			if (!this.index_sessions.TryGetValue(key, out var analysis)) {
				analysis = new reachable_call_analysis(this.modules, token, this, preserve_structural_calls);
				if (thorough_refinement) analysis.budget = new refinement_budget(TimeSpan.FromSeconds(30), long.MaxValue);
			}
			else analysis.renew(token);
			if (budget_override is not null) analysis.budget = budget_override;
			this.index_sessions[key] = analysis;
			var result = index.refine(this, token, preserve_structural_calls, thorough_refinement, analysis); token.ThrowIfCancellationRequested();
			if (analysis.can_resume_index) this.index_sessions[key] = analysis; else this.index_sessions.Remove(key);
			this.record_discoveries(result.all_links_value);
			this.refined_indexes[key] = result; return this.include_discoveries(result, token);
		}
		public MethodDef? resolve(IMethod reference) {
			if (!this.resolution_attempted.Add(reference) && this.resolved.TryGetValue(reference, out var known)) return known;
			var result = call_graph_index.resolve(reference, this.resolved);
			if (result is not null && this.modules.Contains(result.Module)) return result;
			var declaration = reference.DeclaringType;
			if (declaration is TypeSpec specification && specification.TypeSig is GenericInstSig instance) declaration = instance.GenericType.TypeDefOrRef;
			if (!this.types_value.TryGetValue(call_graph_index.safe_type_name(declaration), out var candidates)) return result;
			var signature = call_signature.@for(reference);
			var matches = candidates.Where(type => declaration?.DefinitionAssembly is null ||
				StringComparer.OrdinalIgnoreCase.Equals(type.DefinitionAssembly?.FullName, declaration.DefinitionAssembly.FullName))
				.SelectMany(type => type.Methods).Where(method => method.Name == reference.Name &&
					(new SigComparer().Equals(method.MethodSig, reference.MethodSig) || new SigComparer().Equals(method.MethodSig, signature))).Take(2).ToArray();
			if (matches.Length == 1) this.resolved[reference] = result = matches[0];
			return result;
		}
		public method_structure structure(MethodDef method, CancellationToken token) {
			if (this.structures.TryGetValue(method, out var cached)) return cached;
			token.ThrowIfCancellationRequested();
			var calls = new List<Instruction>();
			var addresses = new List<Instruction>();
			var reads = new HashSet<FieldDef>();
			var writes = new HashSet<FieldDef>();
			bool unknown = !method.HasBody, incomplete = false;
			if (method.HasBody) {
				var instructions = method.Body.Instructions;
				int count = instructions.Count;
				for (int position = 0; position < count; position++) {
					if ((position & 255) == 0) token.ThrowIfCancellationRequested();
					var instruction = instructions[position]; var code = instruction.OpCode.Code;
					if (code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj or Code.Jmp) calls.Add(instruction);
					if (code is Code.Ldftn or Code.Ldvirtftn) addresses.Add(instruction);
					if (instruction.Operand is IField reference) {
						FieldDef? field;
						try { field = reference.ResolveFieldDef(); } catch (Exception) { field = null; }
						if (field is null) unknown = true;
						else if (code is Code.Stfld or Code.Stsfld) writes.Add(field);
						else reads.Add(field);
					}
					if (code is Code.Calli or Code.Cpblk or Code.Initblk or Code.Stobj or Code.Cpobj or Code.Initobj ||
						instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal)) unknown = true;
				}
			}
			var result = new method_structure(calls.ToArray(), reads.ToArray(), writes.ToArray(), unknown || incomplete, incomplete, addresses.ToArray());
			this.structures.TryAdd(method, result); if (this.call_structures.TryAdd(method, result)) this.scanned_method_count++;
			return result;
		}
		public method_structure call_structure(MethodDef method, CancellationToken token) {
			if (this.call_structures.TryGetValue(method, out var cached)) return cached;
			token.ThrowIfCancellationRequested(); var calls = new List<Instruction>(); var addresses = new List<Instruction>();
			if (method.HasBody) {
				var instructions = method.Body.Instructions;
				for (int position = 0; position < instructions.Count; position++) {
					if ((position & 255) == 0) token.ThrowIfCancellationRequested();
					var instruction = instructions[position]; var code = instruction.OpCode.Code;
					if (code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj or Code.Jmp) calls.Add(instruction);
					if (code is Code.Ldftn or Code.Ldvirtftn) addresses.Add(instruction);
				}
			}
			var result = new method_structure(calls.ToArray(), Array.Empty<FieldDef>(), Array.Empty<FieldDef>(), true, false, addresses.ToArray());
			if (this.call_structures.TryAdd(method, result)) this.scanned_method_count++;
			return result;
		}
		public call_graph_slice structural_slice(MethodDef root, int depth, int limit, call_graph_filter_options filter, CancellationToken token,
			call_graph_slice? refinement = null, call_graph_slice? baseline = null) {
			limit = Math.Max(2, limit); depth = Math.Max(1, Math.Min(depth, limit));
			var layers = baseline?.nodes.ToDictionary(node => node.method, node => node.layer) ?? new Dictionary<MethodDef, int> { [root] = 0 };
			var pending = new Queue<MethodDef>(baseline is null ? layers.Keys : baseline.nodes.Where(node => node.is_expanded).Select(node => node.method));
			var links = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			var sites = new List<call_site_info>(); var diagnostics = new List<graph_diagnostic>();
			var refined_links = refinement is null ? null : refinement.links.Concat(call_site_info.to_links(refinement.call_sites)).GroupBy(link => (link.source, link.target))
				.Select(group => { var accumulator = new call_site_accumulator(); foreach (var link in group) accumulator.add(link); return workspace_analysis.link(group.Key.source, group.Key.target, accumulator); }).ToLookup(link => link.source);
			var refined_notes = refinement?.diagnostics.ToLookup(note => (method: note.method, offset: note.offset));
			var expanded = new HashSet<MethodDef>(); var hints = baseline?.nodes.Where(node => !node.is_expanded).ToDictionary(node => node.method, node => node.expansion_hint) ?? new Dictionary<MethodDef, string>();
			if (baseline is not null) diagnostics.AddRange(baseline.diagnostics.Where(note => note.kind is graph_diagnostic_kind.view_depth or graph_diagnostic_kind.assembly_boundary or graph_diagnostic_kind.workspace_boundary));
			var visible_filter = new call_graph_filter_options(filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors,
				filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count);
			bool truncated = false;
			while (pending.Count != 0) {
				token.ThrowIfCancellationRequested();
				var method = pending.Dequeue(); int layer = layers[method];
				if (expanded.Contains(method)) continue;
				if (layer >= depth || !this.modules.Contains(method.Module) || filter.only_current_assembly && !ReferenceEquals(method, root) && !workspace_analysis.same_assembly(root, method)) {
					int remaining = method.HasBody ? this.call_structure(method, token).calls.Length + this.call_structure(method, token).address_references.Length : 0;
					if (remaining != 0) {
						truncated = true;
						var kind = !this.modules.Contains(method.Module) ? graph_diagnostic_kind.workspace_boundary : filter.only_current_assembly && !workspace_analysis.same_assembly(root, method) ? graph_diagnostic_kind.assembly_boundary : graph_diagnostic_kind.view_depth;
						string boundary = kind == graph_diagnostic_kind.workspace_boundary ? ui_text.pick("Outside workspace", "Вне workspace") : kind == graph_diagnostic_kind.assembly_boundary ? ui_text.pick("Assembly boundary", "Граница сборки") : ui_text.pick($"View boundary: depth {depth}", $"Граница просмотра: глубина {depth}");
						hints[method] = boundary + ui_text.pick($"; {remaining} IL relations — Explore from here", $"; IL-связей: {remaining} — исследовать отсюда");
						diagnostics.Add(new graph_diagnostic(method, 0, kind, hints[method]));
					}
					else if (!method.HasBody) diagnostics.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.native_boundary, "No IL body; native/runtime implementation or unloaded metadata"));
					continue;
				}
				expanded.Add(method);
				if (!method.HasBody) diagnostics.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.native_boundary,
					method.IsPinvokeImpl ? "Native implementation: " + method.ImplMap?.Module?.Name + "!" + method.ImplMap?.Name : "No IL body; runtime implementation or unloaded metadata"));
				var structure = this.call_structure(method, token);
				var targets = new HashSet<MethodDef>();
				var extra_links = (refined_links?[method] ?? Enumerable.Empty<call_graph_link>()).Concat(this.discovered_outgoing(method)).ToArray();
				var extra_sites = extra_links.SelectMany(link => link.evidence.Where(item => item.operation != "type initializer").Select(item => (target: link.target, evidence: item))).ToLookup(item => item.evidence.offset);
				if (structure.is_incomplete) { truncated = true; diagnostics.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.analysis_budget, "structural method instruction limit reached")); }
				foreach (var instruction in structure.calls) {
					var reference = instruction.Operand as IMethod;
					var target = reference is null ? null : this.resolve(reference);
					string operation = instruction.OpCode.Name;
					bool potential = instruction.OpCode.Code == Code.Callvirt && target?.IsVirtual == true;
					string reason = potential ? "IL virtual declaration; runtime implementation is not yet refined" : "structural IL operand; reachability is not yet refined";
					var extra = extra_sites[instruction.Offset].ToArray();
					var candidates = (target is null ? Array.Empty<MethodDef>() : new[] { target }).Concat(extra.Select(link => link.target)).Distinct().ToArray();
					var unresolved = refined_notes?[(method: method, offset: instruction.Offset)].Where(note => note.kind is graph_diagnostic_kind.unresolved_pointer or graph_diagnostic_kind.unresolved_delegate or graph_diagnostic_kind.unresolved_reflection or graph_diagnostic_kind.unresolved_reference or graph_diagnostic_kind.runtime_selection).ToArray() ?? Array.Empty<graph_diagnostic>();
					bool unknown_target = candidates.Length == 0 || unresolved.Length != 0 || instruction.OpCode.Code == Code.Calli && extra.Length != 0 && extra.All(item => item.evidence.is_contextual);
					sites.Add(new call_site_info(method, instruction.Offset, operation, reference?.FullName ?? instruction.Operand?.ToString() ?? string.Empty,
						candidates, (target is null ? Array.Empty<call_site_evidence>() : new[] { new call_site_evidence(instruction.Offset, operation, potential, reason, target, call_evidence_kind.il_operand) })
							.Concat(extra.Select(item => item.evidence)).Distinct().ToArray(),
						unknown_target ? unresolved.Length == 0 ? "call target has not been resolved" : string.Join("; ", unresolved.Select(note => note.detail)) : potential ? reason : null,
						reference, unknown_target));
					if (candidates.Length == 0) {
						diagnostics.Add(new graph_diagnostic(method, instruction.Offset, instruction.OpCode.Code == Code.Calli ? graph_diagnostic_kind.unresolved_pointer : graph_diagnostic_kind.unresolved_reference, "structural call target is unresolved"));
						continue;
					}
					if (target is not null) {
						call_graph_index.add_call_site(links, method, target, instruction.Offset, operation, potential,
							potential ? reason : "explicit IL call operand; execution is not implied", call_evidence_kind.il_operand);
						targets.Add(target);
					}
				}
				foreach (var link in extra_links) {
					foreach (var item in link.evidence) call_graph_index.add_call_site(links, method, link.target, item.offset, item.operation, item.is_potential, item.reason, item.kind, item.is_contextual);
					targets.Add(link.target);
				}
				foreach (var target in targets) {
					if (links[(source: method, target: target)].count < filter.minimum_call_site_count || !visible_filter.includes(root, target)) continue;
					if (layers.TryGetValue(target, out int previous_layer)) {
						if (previous_layer > layer + 1) { layers[target] = layer + 1; pending.Enqueue(target); }
						continue;
					}
					if (layers.Count >= limit) { truncated = true; continue; }
					layers.Add(target, layer + 1); pending.Enqueue(target);
				}
			}
			var links_by_source = links.ToLookup(pair => pair.Key.source);
			foreach (var method in expanded) {
				var hidden = links_by_source[method].Where(pair => !layers.ContainsKey(pair.Key.target)).ToArray();
				int filtered = hidden.Count(pair => pair.Value.count < filter.minimum_call_site_count || !visible_filter.includes(root, pair.Key.target));
				int limited = hidden.Length - filtered;
				if (limited != 0) diagnostics.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.node_limit, ui_text.pick($"Hidden by card limit: {limited}", $"Скрыто лимитом карточек: {limited}")));
				if (filtered != 0) diagnostics.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.filtered_target, ui_text.pick($"Hidden by filters: {filtered}", $"Скрыто фильтрами: {filtered}")));
				if (hidden.Length != 0) hints[method] = string.Join("; ", diagnostics.Where(note => note.method == method && note.kind is graph_diagnostic_kind.node_limit or graph_diagnostic_kind.filtered_target).Select(note => note.detail));
			}
			foreach (var method in expanded.ToArray()) {
				foreach (var instruction in this.call_structure(method, token).address_references) {
					token.ThrowIfCancellationRequested();
					if (instruction.Operand is not IMethod reference) continue;
					var target = this.resolve(reference);
					diagnostics.Add(new graph_diagnostic(method, instruction.Offset, graph_diagnostic_kind.address_reference, instruction.OpCode.Name + " — address operand, not an invocation: " + reference.FullName, target));
					if (target is null) { diagnostics.Add(new graph_diagnostic(method, instruction.Offset, graph_diagnostic_kind.unresolved_reference, "Address reference metadata unavailable: " + reference.FullName)); continue; }
					if (layers.ContainsKey(target)) continue;
					if (!visible_filter.includes(root, target)) { diagnostics.Add(new graph_diagnostic(method, instruction.Offset, graph_diagnostic_kind.filtered_target, "Address reference hidden by filters", target)); continue; }
					if (layers.Count >= limit) { truncated = true; diagnostics.Add(new graph_diagnostic(method, instruction.Offset, graph_diagnostic_kind.node_limit, "Address reference outside card limit", target)); continue; }
					layers.Add(target, layers[method] + 1);
					hints[target] = ui_text.pick("Address reference, not a call — Explore from here", "Ссылка на адрес, не вызов — исследовать отсюда");
				}
			}
			return new call_graph_slice(layers.Select(pair => new call_graph_node(pair.Key, pair.Value, pair.Key == root, !this.modules.Contains(pair.Key.Module), hints.GetValueOrDefault(pair.Key) ?? string.Empty, expanded.Contains(pair.Key))).ToArray(),
				links.Where(pair => layers.ContainsKey(pair.Key.target) && pair.Value.count >= filter.minimum_call_site_count)
					.Select(pair => workspace_analysis.link(pair.Key.source, pair.Key.target, pair.Value)).ToArray(), truncated || refinement?.is_truncated == true,
					sites.Count(site => site.operation == "calli" && site.has_unknown_targets), diagnostics.Concat(refinement?.diagnostics.Where(note => expanded.Contains(note.method) && note.kind != graph_diagnostic_kind.address_reference) ?? Array.Empty<graph_diagnostic>()).Distinct().ToArray(), sites, refinement is null, refinement?.can_continue_refinement == true);
		}
		static bool same_assembly(MethodDef root, MethodDef method) => ReferenceEquals(root.Module, method.Module) || root.Module.Assembly is not null && ReferenceEquals(root.Module.Assembly, method.Module.Assembly);
		public (call_graph_index index, call_graph_slice slice) browse(MethodDef root, int limit, call_graph_filter_options filter, CancellationToken token, IProgress<call_graph_slice>? progress = null, int depth = 2, bool resume_refinement = true, refinement_budget? budget_override = null) {
			depth = Math.Max(1, Math.Min(8, depth));
			string filter_key = "browse:" + filter.hide_system_methods + ":" + filter.hide_accessors + ":" + filter.hide_constructors + ":" + filter.only_current_namespace + ":" + filter.only_current_type + ":" + filter.require_method_body + ":" + filter.minimum_call_site_count + ":" + filter.only_current_assembly;
			var key = (root: root, depth: depth, limit: limit, filter_key: filter_key);
			if (this.queries.TryGetValue(key, out var cached) && (!resume_refinement || !cached.slice.can_continue_refinement)) {
				if (this.query_revisions.GetValueOrDefault(key) == this.discovery_revision) return cached;
				var refreshed = this.structural_slice(root, depth, limit, filter, token, cached.slice);
				var updated = (call_graph_index.from_slice(root, this.modules, refreshed), refreshed);
				this.store_query(key, updated); return updated;
			}
			var baseline = this.structural_slice(root, depth, limit, filter, token); progress?.Report(baseline);
			var options = new call_graph_filter_options(filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors,
				filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count, filter.only_current_assembly, true);
			call_graph_slice refinement;
			if (!this.sessions.TryGetValue(key, out var analysis)) { analysis = new reachable_call_analysis(this.modules, token, this); if (this.create_refinement_budget is not null) analysis.budget = this.create_refinement_budget(); }
			else analysis.renew(token);
			if (budget_override is not null) analysis.budget = budget_override;
			this.store_session(key, analysis, true);
			try { refinement = analysis.select(root, depth, limit, options); }
			catch (OperationCanceledException) { throw; }
			catch (Exception error) {
				refinement = new call_graph_slice(Array.Empty<call_graph_node>(), Array.Empty<call_graph_link>(), true, 0,
					new[] { new graph_diagnostic(root, 0, graph_diagnostic_kind.unsupported_instruction, "target refinement failed; IL references retained: " + error.GetType().Name) });
			}
			var slice = this.structural_slice(root, depth, limit, filter, token, refinement, baseline); token.ThrowIfCancellationRequested();
			var coverage = structural_coverage.check(this, slice, token);
			if (coverage.Length != 0) slice = new call_graph_slice(slice.nodes, slice.links, true, slice.unresolved_indirect_call_count, slice.diagnostics.Concat(coverage.Select(issue => new graph_diagnostic(issue.method, issue.offset, graph_diagnostic_kind.invalid_il, "Internal IL coverage check: " + issue.detail))).ToArray(), slice.call_sites, slice.is_structural, slice.can_continue_refinement);
			this.record_discoveries(slice.links.Concat(call_site_info.to_links(slice.call_sites)), true);
			var result = (call_graph_index.from_slice(root, this.modules, slice), slice: slice);
			this.store_session(key, analysis, slice.can_continue_refinement); this.store_query(key, result); this.analysis_count++;
			return result;
		}
		void store_session((MethodDef, int, int, string) key, reachable_call_analysis analysis, bool incomplete) {
			if (!incomplete) { this.sessions.Remove(key); return; }
			if (!this.sessions.ContainsKey(key) && this.sessions.Count >= 4) this.sessions.Remove(this.sessions.Keys.First());
			this.sessions[key] = analysis;
		}
		void store_query((MethodDef, int, int, string) key, (call_graph_index, call_graph_slice) result) {
			if (!this.queries.ContainsKey(key) && this.queries.Count >= 24) { var oldest = this.queries.Keys.First(); this.queries.Remove(oldest); this.query_revisions.Remove(oldest); this.sessions.Remove(oldest); }
			this.queries[key] = result;
			this.query_revisions[key] = this.discovery_revision;
		}
		internal static call_graph_link link(MethodDef source, MethodDef target, call_site_accumulator accumulator) =>
			new call_graph_link(source, target, accumulator.count, accumulator.offsets.OrderBy(offset => offset).ToArray(),
				accumulator.operations.OrderBy(value => value, StringComparer.Ordinal).ToArray(), !accumulator.has_direct,
				accumulator.evidence.OrderBy(item => item.offset).ThenBy(item => item.operation, StringComparer.Ordinal).ToArray());
		public (call_graph_index index, call_graph_slice slice) analyze(MethodDef root, int depth, int limit, call_graph_filter_options filter,
			IProgress<call_graph_slice>? progress, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			string filter_key =
				filter.hide_system_methods + ":" + filter.only_current_assembly + ":" + filter.hide_accessors + ":" + filter.hide_constructors + ":" +
				filter.only_current_namespace + ":" + filter.only_current_type + ":" + filter.require_method_body + ":" + filter.minimum_call_site_count + ":" + filter.preserve_structural_calls + ":" + filter.thorough_refinement;
			var key = (root: root, depth: depth, limit: limit, filter_key: filter_key);
			if (this.queries.TryGetValue(key, out var cached) && !cached.slice.can_continue_refinement) return cached;
			var structural = this.structural_slice(root, depth, limit, filter, token);
			progress?.Report(structural);
			if (!this.sessions.TryGetValue(key, out var analysis)) { analysis = new reachable_call_analysis(this.modules, token, this); if (this.create_refinement_budget is not null) analysis.budget = this.create_refinement_budget(); }
			else analysis.renew(token);
			this.store_session(key, analysis, true);
			var slice = analysis.select(root, depth, limit, filter);
			token.ThrowIfCancellationRequested();
			var index = call_graph_index.from_slice(root, this.modules, slice);
			this.record_discoveries(slice.links.Concat(call_site_info.to_links(slice.call_sites)), true);
			this.store_session(key, analysis, slice.can_continue_refinement); this.store_query(key, (index, slice)); this.analysis_count++;
			return (index: index, slice: slice);
		}
		public call_site_info[] describe_sites(call_graph_slice slice, CancellationToken token) {
			var evidence = slice.call_sites.Concat(call_site_info.from_links(slice.links)).GroupBy(site => (site.source, site.offset)).ToDictionary(group => group.Key, group => group.First() with {
				targets = group.SelectMany(site => site.targets).Distinct().ToArray(), evidence = group.SelectMany(site => site.evidence).Distinct().ToArray(),
				has_unknown_targets = group.Any(site => site.has_unknown_targets),
			});
			foreach (var node in slice.nodes.Where(node => this.modules.Contains(node.method.Module))) {
				var method_notes = slice.diagnostics_for(node.method).Where(item => item.kind is not graph_diagnostic_kind.address_reference).ToLookup(item => item.offset);
				foreach (var instruction in this.call_structure(node.method, token).calls) {
					var key = (method: node.method, offset: instruction.Offset);
					var notes = method_notes[instruction.Offset].ToArray();
					if (evidence.TryGetValue(key, out var site)) evidence[key] = site with {
						declared_target = instruction.Operand?.ToString() ?? string.Empty,
						uncertainty = notes.Length == 0 ? site.uncertainty : string.Join("; ", notes.Select(item => item.detail)),
						reference = instruction.Operand as IMethod,
							has_unknown_targets = site.has_unknown_targets || notes.Any(item => item.kind is graph_diagnostic_kind.unresolved_pointer or graph_diagnostic_kind.unresolved_delegate or graph_diagnostic_kind.unresolved_reflection or graph_diagnostic_kind.runtime_selection)
					};
					else {
						var reference = instruction.Operand as IMethod; var target = reference is null ? null : this.resolve(reference);
						evidence[key] = new call_site_info(node.method, instruction.Offset, instruction.OpCode.Name, instruction.Operand?.ToString() ?? string.Empty,
							target is null ? Array.Empty<MethodDef>() : new[] { target }, target is null ? Array.Empty<call_site_evidence>() : new[] { new call_site_evidence(instruction.Offset, instruction.OpCode.Name, true, "IL operand outside selected links; execution is not implied", target, call_evidence_kind.il_operand) },
							notes.Length == 0 ? "not displayed: unreachable, filtered, outside depth or analysis incomplete" : string.Join("; ", notes.Select(item => item.detail)), reference, target is null);
					}
				}
			}
			return evidence.Values.OrderBy(site => site.source.MDToken.Raw).ThenBy(site => site.offset).ToArray();
		}
	}
}
