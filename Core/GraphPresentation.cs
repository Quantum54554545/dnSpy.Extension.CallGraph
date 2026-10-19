using System;
using System.Collections.Generic;

namespace dnSpy.CallGraph.Core {
	enum GraphLinkKind {
		Call,
		PossibleCall,
		FallThrough,
		Conditional,
		Switch,
		Exception,
	}

	sealed class GraphNodePresentation {
		public object Reference { get; }
		public int Layer { get; }
		public bool IsRoot { get; }
		public bool IsExternal { get; }
		public string Title { get; }
		public string Subtitle { get; }
		public string ToolTip { get; }
		public string CopyText { get; }

		public GraphNodePresentation(object reference, int layer, bool is_root, bool is_external,
			string title, string subtitle, string tool_tip, string copy_text) {
			Reference = reference;
			Layer = layer;
			IsRoot = is_root;
			IsExternal = is_external;
			Title = title;
			Subtitle = subtitle;
			ToolTip = tool_tip;
			CopyText = copy_text;
		}
	}

	sealed class GraphLinkPresentation {
		public object Source { get; }
		public object Target { get; }
		public int CallSiteCount { get; }
		public string ToolTip { get; }
		public string Label { get; }
		public GraphLinkKind Kind { get; }

		public GraphLinkPresentation(object source, object target, int call_site_count, string tool_tip,
			string label = "", GraphLinkKind kind = GraphLinkKind.Call) {
			Source = source;
			Target = target;
			CallSiteCount = call_site_count;
			ToolTip = tool_tip;
			Label = label;
			Kind = kind;
		}
	}

	sealed class GraphPresentation {
		public IReadOnlyList<GraphNodePresentation> Nodes { get; }
		public IReadOnlyList<GraphLinkPresentation> Links { get; }

		public GraphPresentation(IReadOnlyList<GraphNodePresentation> nodes,
			IReadOnlyList<GraphLinkPresentation> links) {
			Nodes = nodes;
			Links = links;
		}
	}

	sealed class GraphReferenceEventArgs : EventArgs {
		public object Reference { get; }
		public GraphReferenceEventArgs(object reference) => Reference = reference;
	}
}
