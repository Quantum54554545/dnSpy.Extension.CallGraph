using System.ComponentModel.Composition;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph {
	interface ICallGraphService {
		void ShowCurrentSelection();
		void Show(MethodDef method);
	}

	[Export(typeof(ICallGraphService))]
	sealed class CallGraphService : ICallGraphService {
		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly CallGraphSettings settings;
		readonly IThemeService theme_service;
		IDocumentTab? graph_tab;

		[ImportingConstructor]
		public CallGraphService(IDocumentTabService document_tab_service, IDecompilerService decompiler_service,
			CallGraphSettings settings, IThemeService theme_service) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
		}

		public void ShowCurrentSelection() {
			
			
			object? reference = CallGraphControl.GetCurrentMethod(document_tab_service);
			ShowCore(reference);
		}

		public void Show(MethodDef method) => ShowCore(method);

		void ShowCore(object? reference) {
			var tab = graph_tab;
			if (tab is null || !document_tab_service.SortedTabs.Contains(tab)) {
				tab = document_tab_service.OpenEmptyTab();
				graph_tab = tab;
			}

			document_tab_service.ActiveTab = tab;
			tab.Show(new CallGraphDocumentTabContent(document_tab_service, decompiler_service, settings, theme_service, reference), null,
				_ => tab.TrySetFocus());
		}
	}
}
