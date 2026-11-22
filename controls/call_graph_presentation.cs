using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	sealed record call_graph_view(graph_presentation graph, IReadOnlyDictionary<object, highlighted_snippet> snippets);
	static class call_graph_presentation {
		internal static call_graph_view build(call_graph_slice slice, IReadOnlyDictionary<MethodDef, highlighted_snippet> snippets, CancellationToken token = default) {
			token.ThrowIfCancellationRequested();
			var visible = slice.nodes.ToDictionary(node => node.method);
			var sites_by_source = slice.call_sites.ToLookup(site => site.source);
			var links_by_source = slice.links.ToLookup(link => link.source);
			var addresses = slice.diagnostics.Where(note => slice.direction != call_graph_direction.callers && note.kind == graph_diagnostic_kind.address_reference).DistinctBy(note => (method: note.method, offset: note.offset, target: note.target)).ToArray();
			var unknown = slice.call_sites.Where(site => slice.direction != call_graph_direction.callers && (site.targets.Count == 0 || site.has_unknown_targets) && visible.TryGetValue(site.source, out var node) && node.is_expanded)
				.GroupBy(site => site.operation == "calli" ? (object)(source: site.source, offset: site.offset) : (full_name: site.reference?.DeclaringType?.DefinitionAssembly?.FullName, declared_target: site.declared_target))
				.Take(128).ToArray();
			var rendered_unknown_sites = unknown.SelectMany(group => group).Select(site => (source: site.source, offset: site.offset)).ToHashSet();
			var addresses_by_source = addresses.ToLookup(note => note.method);
			var unknown_addresses = addresses.Where(note => note.target is null && visible.ContainsKey(note.method)).Take(128).ToArray();
			var rendered_unknown_addresses = unknown_addresses.Select(note => (method: note.method, offset: note.offset)).ToHashSet();
			var nodes = slice.nodes.Select(node => {
				token.ThrowIfCancellationRequested();
				var method_sites = sites_by_source[node.method]; var method_links = links_by_source[node.method];
				var targets = method_sites.SelectMany(site => site.targets).Concat(method_links.Select(link => link.target)).Distinct().ToArray();
				int shown = method_links.Select(link => link.target).Distinct().Count();
				int unresolved = method_sites.Count(site => site.targets.Count == 0 || site.has_unknown_targets);
				string counts = node.is_expanded ? ui_text.pick($"Targets: {targets.Length}, shown: {shown}, unresolved sites: {unresolved}", $"Целей: {targets.Length}, на графе: {shown}, не определено мест: {unresolved}") : string.Empty;
				int address_count = addresses_by_source[node.method].Count();
				if (address_count != 0) counts += ui_text.pick($"; address references: {address_count}", $"; ссылок на адреса: {address_count}");
				string subtitle = node.expansion_hint.Length != 0 ? node.expansion_hint : counts;
				if (node.expansion_hint.Length != 0 && address_count != 0) subtitle += ui_text.pick($"; address references: {address_count}", $"; ссылок на адреса: {address_count}");
				var boundaries = slice.diagnostics_for(node.method).Where(note => note.kind is graph_diagnostic_kind.node_limit or graph_diagnostic_kind.filtered_target or graph_diagnostic_kind.native_boundary).Select(note => note.detail).Distinct().Take(3).ToArray();
				if (boundaries.Length != 0) subtitle += " • " + string.Join("; ", boundaries);
				bool hidden_unknown = method_sites.Any(site => (site.targets.Count == 0 || site.has_unknown_targets) && !rendered_unknown_sites.Contains((source: site.source, offset: site.offset))) || addresses_by_source[node.method].Any(note => note.target is null && !rendered_unknown_addresses.Contains((method: note.method, offset: note.offset)));
				if (hidden_unknown)
					subtitle += ui_text.pick(" • some unresolved targets outside card limit", " • часть неизвестных целей вне лимита карточек");
				return new graph_node_presentation(node.method, node.layer, node.is_root, node.is_external, node.method.Name,
					subtitle + " • " + (node.method.DeclaringType?.FullName ?? "<Module>"),
					call_graph_index.signature(node.method) + $"   token 0x{node.method.MDToken.Raw:X8}" + analysis_details.tool_tip(slice, node.method), call_graph_index.display_name(node.method), hidden_unknown && node.expansion_hint.Length == 0 ? ui_text.pick("More unresolved sites — open evidence", "Остальные неопределённые места — открыть основания") : node.expansion_hint, hidden_unknown && node.expansion_hint.Length == 0);
			}).ToList();
			var links = slice.links.SelectMany(link => call_graph_presentation.present_links(link, token)).ToList();
			foreach (var group in addresses.Where(note => note.target is not null && visible.ContainsKey(note.target)).GroupBy(note => (method: note.method, target: note.target)))
				links.Add(new graph_link_presentation(group.Key.method, group.Key.target!, group.Count(), string.Join("\n", group.Select(analysis_details.describe)), group.Any(note => note.detail.StartsWith("ldvirtftn", StringComparison.Ordinal)) ? "ldvirtftn / &" : "ldftn / &", graph_link_kind.address_reference));
			var rendered = snippets.ToDictionary(pair => (object)pair.Key, pair => pair.Value);
			foreach (var note in unknown_addresses) {
				var target = new unresolved_call_target(note.method, note.offset, "method address", note.detail);
				string description = ui_text.pick("Address operand exists; metadata unavailable. Not an invocation.", "Ссылка на адрес есть; метаданные недоступны. Это не вызов.");
				nodes.Add(new graph_node_presentation(target, visible[note.method].layer + 1, false, true, "? &", description, analysis_details.describe(note), note.detail));
				rendered[target] = highlighted_snippet.plain(note.detail + "\n" + description);
				links.Add(new graph_link_presentation(note.method, target, 1, analysis_details.describe(note), "& ?", graph_link_kind.address_reference));
			}
			foreach (var group in unknown) {
				token.ThrowIfCancellationRequested();
				var first = group.First();
				var target = new unresolved_call_target(first.source, first.offset, first.operation, first.declared_target);
				string title = first.reference?.Name?.ToString() ?? ui_text.pick("Unknown call target", "Неизвестная цель вызова");
				string description = first.targets.Count != 0 ? ui_text.pick("Remaining runtime targets are unresolved", "Оставшиеся runtime-цели не определены") :
					first.operation == "calli" ? ui_text.pick("Function pointer is unresolved", "Адрес функции не определён") : ui_text.pick("Metadata unavailable — IL reference exists", "Метаданные недоступны — ссылка в IL есть");
				nodes.Add(new graph_node_presentation(target, group.Min(site => visible[site.source].layer) + 1, false, true, "? " + title, description,
					first.declared_target, first.declared_target));
				string reasons = string.Join("\n", group.Select(site => site.uncertainty).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct().Take(6));
				rendered[target] = highlighted_snippet.plain(first.operation + " " + first.declared_target + "\n" + description + "\n" + reasons);
				foreach (var source in group.GroupBy(site => site.source)) links.Add(new graph_link_presentation(source.Key, target, source.Count(),
					string.Join("\n", source.Take(32).Select(site => $"IL_{site.offset:X4}: {site.operation} {site.declared_target}")) + call_graph_presentation.remaining_sites(source.Count()), "?", graph_link_kind.possible_call));
			}
			return new call_graph_view(new graph_presentation(nodes, links), rendered);
		}
		static string remaining_sites(int count) => count <= 32 ? "" : ui_text.pick($"\n… {count - 32} more sites — open call-site evidence", $"\n… ещё мест: {count - 32} — открой места вызовов и основания");
		static IEnumerable<graph_link_presentation> present_links(call_graph_link link, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			var sites = link.evidence.GroupBy(item => (offset: item.offset, initializer: item.operation == "type initializer")).Select(site => (
initializer: site.Key.initializer,
kind: site.Any(item => item.kind == call_evidence_kind.resolved_target && !item.is_contextual) ? call_evidence_kind.resolved_target : site.Any(item => item.kind == call_evidence_kind.possible_target) ? call_evidence_kind.possible_target : call_evidence_kind.il_operand,
evidence: site.ToArray()));
			foreach (var group in sites.GroupBy(site => (kind: site.kind, initializer: site.initializer))) {
				token.ThrowIfCancellationRequested();
				int site_count = group.Count();
				var evidence = group.Take(32).Select(site => site.evidence.FirstOrDefault(item => item.kind == site.kind && !item.is_contextual) ?? site.evidence[0]).ToArray();
				yield return new graph_link_presentation(link.source, link.target, site_count,
					$"{call_graph_index.display_name(link.source)} → {call_graph_index.display_name(link.target)}\n" + string.Join("\n", evidence.Select(item => $"IL_{item.offset:X4}: {item.operation} — {item.reason}")) + call_graph_presentation.remaining_sites(site_count) +
					ui_text.pick("\nStatic relation, not proof of execution", "\nСтатическая связь, не доказательство выполнения"),
					group.Key.initializer ? ".cctor" : group.Key.kind == call_evidence_kind.il_operand ? "IL" : (group.Key.kind == call_evidence_kind.possible_target ? "? " : "") + string.Join("/", evidence.Select(item => item.operation).Distinct().Take(2)),
					group.Key.initializer ? graph_link_kind.initialization_dependency : group.Key.kind == call_evidence_kind.il_operand ? graph_link_kind.structural_call : group.Key.kind == call_evidence_kind.possible_target ? graph_link_kind.possible_call : graph_link_kind.call);
			}
		}
	}
}
