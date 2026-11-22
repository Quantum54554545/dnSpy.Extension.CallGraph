using System;
using System.Collections.Generic;

namespace dnSpy.CallGraph.Core {
	enum graph_link_kind {
		call,
		possible_call,
		structural_call,
		address_reference,
		value_dependency,
		initialization_dependency,
		fall_through,
		conditional,
		@switch,
		exception,
	}

	sealed class graph_node_presentation {
		public object reference { get; }
		public int layer { get; }
		public bool is_root { get; }
		public bool is_external { get; }
		public string title { get; }
		public string subtitle { get; }
		public string tool_tip { get; }
		public string copy_text { get; }
		public string continuation { get; }
		public bool continuation_shows_evidence { get; }

		public graph_node_presentation(object reference, int layer, bool is_root, bool is_external,
			string title, string subtitle, string tool_tip, string copy_text, string continuation = "", bool continuation_shows_evidence = false) {
			this.reference = reference;
			this.layer = layer;
			this.is_root = is_root;
			this.is_external = is_external;
			this.title = title;
			this.subtitle = subtitle;
			this.tool_tip = tool_tip;
			this.copy_text = copy_text;
			this.continuation = continuation;
			this.continuation_shows_evidence = continuation_shows_evidence;
		}
	}

	sealed class graph_link_presentation {
		public object source { get; }
		public object target { get; }
		public int call_site_count { get; }
		public string tool_tip { get; }
		public string label { get; }
		public graph_link_kind kind { get; }

		public graph_link_presentation(object source, object target, int call_site_count, string tool_tip,
			string label = "", graph_link_kind kind = graph_link_kind.call) {
			this.source = source;
			this.target = target;
			this.call_site_count = call_site_count;
			this.tool_tip = tool_tip;
			this.label = label;
			this.kind = kind;
		}
	}

	sealed class graph_presentation {
		public IReadOnlyList<graph_node_presentation> nodes { get; }
		public IReadOnlyList<graph_link_presentation> links { get; }

		public graph_presentation(IReadOnlyList<graph_node_presentation> nodes,
			IReadOnlyList<graph_link_presentation> links) {
			this.nodes = nodes;
			this.links = links;
		}
	}

	sealed class graph_reference_event_args : EventArgs {
		public object reference { get; }
		public graph_reference_event_args(object reference) => this.reference = reference;
	}
}
