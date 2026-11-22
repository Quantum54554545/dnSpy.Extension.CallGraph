using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	enum call_evidence_kind { il_operand, resolved_target, possible_target }
	sealed record call_site_evidence(uint offset, string operation, bool is_potential, string reason, MethodDef? target = null, call_evidence_kind? classification = null, bool is_contextual = false) {
		public call_evidence_kind kind => this.classification ?? (this.is_potential ? call_evidence_kind.possible_target : call_evidence_kind.resolved_target);
	}

	sealed record call_site_info(MethodDef source, uint offset, string operation, string declared_target,
		IReadOnlyList<MethodDef> targets, IReadOnlyList<call_site_evidence> evidence, string? uncertainty, IMethod? reference = null, bool has_unknown_targets = false) {
		public static call_site_info[] from_links(IEnumerable<call_graph_link> links) => links
			.SelectMany(link => link.evidence.Select(evidence => (source: link.source, target: link.target, evidence: evidence)))
			.GroupBy(item => (source: item.source, offset: item.evidence.offset))
			.Select(group => new call_site_info(group.Key.source, group.Key.offset,
				string.Join(", ", group.Select(item => item.evidence.operation).Distinct()), string.Empty,
				group.Select(item => item.target).Distinct().ToArray(), group.Select(item => item.evidence).Distinct().ToArray(),
				group.All(item => item.evidence.is_potential) ? "runtime target or reachability is not proven" : null)).ToArray();
		public static call_graph_link[] to_links(IEnumerable<call_site_info> sites) {
			var links = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			foreach (var site in sites) foreach (var target in site.targets) {
				var key = (site.source, target);
				if (!links.TryGetValue(key, out var accumulator)) links[key] = accumulator = new call_site_accumulator();
				foreach (var evidence in site.evidence.Where(item => item.target == target)) accumulator.add(evidence.offset, evidence.operation, evidence.is_potential, evidence.reason, evidence.classification, evidence.is_contextual);
			}
			return links.Where(pair => pair.Value.count != 0).Select(pair => workspace_analysis.link(pair.Key.source, pair.Key.target, pair.Value)).ToArray();
		}
	}
	sealed record unresolved_call_target(MethodDef source, uint offset, string operation, string declared_target);
}
