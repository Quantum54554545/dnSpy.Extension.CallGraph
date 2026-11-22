using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	sealed record research_group(string name);
	enum research_action { expand, collapse, pin, evidence, sources, alias, note, group, compact, ungroup, expand_group, collapse_group }
	sealed class research_action_event_args : EventArgs {
		public object reference { get; }
		public research_action action { get; }
		public research_action_event_args(object reference, research_action action) { this.reference = reference; this.action = action; }
	}
	static class research_presentation {
		public static call_graph_view apply(call_graph_view view, research_store store, ISet<string> expanded_groups, Func<MethodDef, bool> is_pinned) {
			return research_presentation.apply(view, store.get, expanded_groups, is_pinned);
		}
		public static call_graph_view apply(call_graph_view view, Func<MethodDef, research_annotation> get_annotation, ISet<string> expanded_groups, Func<MethodDef, bool> is_pinned) {
			var replacements = new Dictionary<object, object>(); var nodes = new List<graph_node_presentation>(); var snippets = view.snippets.ToDictionary(pair => pair.Key, pair => pair.Value);
			foreach (var node in view.graph.nodes) {
				if (node.reference is not MethodDef method) { nodes.Add(node); continue; }
				var annotation = get_annotation(method);
				if (!node.is_root && annotation.group.Length != 0 && !expanded_groups.Contains(annotation.group)) { replacements[method] = new research_group(annotation.group); continue; }
				string title = annotation.alias.Length != 0 ? annotation.alias : node.title;
				string subtitle = (is_pinned(method) ? ui_text.pick("[pinned] ", "[закреплён] ") : "") + node.subtitle;
				if (annotation.group.Length != 0) subtitle = "[" + annotation.group + "] " + subtitle;
				if (annotation.note.Length != 0) subtitle = ui_text.pick("[note] ", "[заметка] ") + subtitle;
				nodes.Add(new graph_node_presentation(method, node.layer, node.is_root, node.is_external, title, subtitle, node.tool_tip + (annotation.note.Length == 0 ? "" : "\n\n" + annotation.note), node.copy_text, node.continuation, node.continuation_shows_evidence));
				if (annotation.compact) snippets[method] = highlighted_snippet.plain(string.Empty);
			}
			foreach (var group in replacements.GroupBy(pair => (research_group)pair.Value)) {
				var member_keys = group.Select(pair => pair.Key).ToHashSet();
				var members = view.graph.nodes.Where(node => member_keys.Contains(node.reference)).ToArray();
				string text = string.Join("\n", members.Select(node => node.reference is MethodDef method && get_annotation(method).alias.Length != 0 ? get_annotation(method).alias : node.title));
				nodes.Add(new graph_node_presentation(group.Key, members.Min(node => node.layer), false, false, group.Key.name, ui_text.pick($"Group · {members.Length} methods", $"Группа · методов: {members.Length}"), text, text, ui_text.pick("Expand group", "Развернуть группу")));
				snippets[group.Key] = highlighted_snippet.plain(string.Join("\n", members.Take(8).Select(node => node.title)) + (members.Length > 8 ? ui_text.pick($"\n… {members.Length - 8} more; expand group", $"\n… ещё {members.Length - 8}; разверни группу") : ""));
			}
			object replace(object reference) => replacements.GetValueOrDefault(reference) ?? reference;
			var links = view.graph.links.GroupBy(link => (source: replace(link.source), target: replace(link.target), kind: link.kind)).Where(group => !Equals(group.Key.source, group.Key.target) || group.All(link => Equals(link.source, link.target))).Select(group =>
				new graph_link_presentation(group.Key.source, group.Key.target, group.Sum(link => link.call_site_count), string.Join("\n\n", group.Select(link => link.tool_tip)), string.Join("/", group.Select(link => link.label).Distinct()), group.Key.kind)).ToArray();
			return new call_graph_view(new graph_presentation(nodes, links), snippets);
		}
	}
}
