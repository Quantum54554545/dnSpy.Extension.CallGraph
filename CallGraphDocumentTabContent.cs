using System;
using System.Windows;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph {
		sealed class CallGraphDocumentTabContent : DocumentTabContent {
		static readonly object UIContextKey = typeof(CallGraphDocumentTabUIContext);
		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly CallGraphSettings settings;
		readonly IThemeService theme_service;
		readonly object? initial_reference;

		public override string Title => UiText.CallGraph;
		public override object? ToolTip => initial_reference switch {
			MethodDef method => UiText.CallGraph + ": " + CallGraphIndex.DisplayName(method),
			_ => UiText.Pick("Method call graph, function map and CFG", "Граф вызовов методов, карта функций и CFG"),
		};

		public CallGraphDocumentTabContent(IDocumentTabService document_tab_service,
			IDecompilerService decompiler_service, CallGraphSettings settings,
			IThemeService theme_service, object? initial_reference) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
			this.initial_reference = initial_reference;
		}

		public override DocumentTabContent Clone() =>
			new CallGraphDocumentTabContent(document_tab_service, decompiler_service, settings, theme_service, initial_reference);

		public override DocumentTabUIContext CreateUIContext(IDocumentTabUIContextLocator locator) =>
			locator.Get(UIContextKey, useStrongReference: true,
				creator: () => new CallGraphDocumentTabUIContext(document_tab_service, decompiler_service, settings, theme_service));

		public override void OnShow(IShowContext ctx) {
			var ui_context = (CallGraphDocumentTabUIContext)ctx.UIContext;
			if (initial_reference is MethodDef method)
				ui_context.Control.ShowMethod(method);
			else
				ui_context.Control.ShowNoSelectionMessage();
		}
	}

	sealed class CallGraphDocumentTabUIContext : DocumentTabUIContext, IDisposable {
		public CallGraphControl Control { get; }
		public override IInputElement? FocusedElement => Control;
		
		
		public override FrameworkElement? ZoomElement => null;
		public override object? UIObject => Control;

		public CallGraphDocumentTabUIContext(IDocumentTabService document_tab_service,
			IDecompilerService decompiler_service, CallGraphSettings settings, IThemeService theme_service) =>
			Control = new CallGraphControl(document_tab_service, decompiler_service, settings, theme_service);

		public void Dispose() => Control.DisposeGraph();
	}
}
