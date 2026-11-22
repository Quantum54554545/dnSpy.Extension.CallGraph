using System;
using System.Windows;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph {
	sealed class call_graph_document_tab_content : DocumentTabContent {
		static readonly object ui_context_key = typeof(call_graph_document_tab_ui_context);
		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly call_graph_settings settings;
		readonly IThemeService theme_service;
		readonly call_graph_index_cache index_cache;
		readonly object? initial_reference;
		WeakReference<call_graph_document_tab_ui_context>? active_context;
		object? active_reference => this.active_context is not null && this.active_context.TryGetTarget(out var context)
			? context.control.current_root_value : this.initial_reference;

		public override string Title => ui_text.call_graph;
		public override object? ToolTip => this.active_reference switch {
			MethodDef method => ui_text.call_graph + ": " + call_graph_index.display_name(method),
			_ => ui_text.pick("Method call graph, function map and CFG", "Граф вызовов методов, карта функций и CFG"),
		};

		public call_graph_document_tab_content(IDocumentTabService document_tab_service,
			IDecompilerService decompiler_service, call_graph_settings settings,
			IThemeService theme_service, call_graph_index_cache index_cache, object? initial_reference) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
			this.index_cache = index_cache;
			this.initial_reference = initial_reference;
		}

		public override DocumentTabContent Clone() =>
			new call_graph_document_tab_content(this.document_tab_service, this.decompiler_service, this.settings, this.theme_service, this.index_cache, this.active_reference);

		public override DocumentTabUIContext CreateUIContext(IDocumentTabUIContextLocator locator) =>
			locator.Get(call_graph_document_tab_content.ui_context_key, useStrongReference: true,
				creator: () => new call_graph_document_tab_ui_context(this.document_tab_service, this.decompiler_service, this.settings, this.theme_service, this.index_cache));

		public override void OnShow(IShowContext ctx) {
			var ui_context = (call_graph_document_tab_ui_context)ctx.UIContext;
			if (ctx.IsRefresh && this.active_context is not null && this.active_context.TryGetTarget(out var previous) && ReferenceEquals(previous, ui_context)) return;
			this.active_context = new WeakReference<call_graph_document_tab_ui_context>(ui_context);
			if (this.initial_reference is MethodDef method)
				ui_context.control.show_method(method);
			else
				ui_context.control.show_no_selection_message();
		}
	}

	sealed class call_graph_document_tab_ui_context : DocumentTabUIContext, IDisposable {
		public call_graph_control control { get; }
		public override IInputElement? FocusedElement => this.control;

		public override FrameworkElement? ZoomElement => null;
		public override object? UIObject => this.control;

		public call_graph_document_tab_ui_context(IDocumentTabService document_tab_service,
			IDecompilerService decompiler_service, call_graph_settings settings, IThemeService theme_service,
			call_graph_index_cache index_cache) =>
			this.control = new call_graph_control(document_tab_service, decompiler_service, settings, theme_service, index_cache);

		public void Dispose() => this.control.dispose_graph();
	}
}
