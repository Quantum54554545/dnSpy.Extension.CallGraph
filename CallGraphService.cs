using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
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
		readonly CallGraphIndexCache index_cache = new CallGraphIndexCache();
		IDocumentTab? graph_tab;

		[ImportingConstructor]
		public CallGraphService(IDocumentTabService document_tab_service, IDecompilerService decompiler_service,
			CallGraphSettings settings, IThemeService theme_service) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
			document_tab_service.DocumentModified += DocumentTabService_DocumentModified;
			document_tab_service.DocumentCollectionChanged += DocumentTabService_DocumentCollectionChanged;
		}

		void DocumentTabService_DocumentModified(object? sender, DocumentModifiedEventArgs e) =>
			index_cache.Invalidate(e.Documents.GetModules<ModuleDef>().Distinct());

		void DocumentTabService_DocumentCollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			var loaded_modules = new HashSet<ModuleDef>(document_tab_service.DocumentTreeView.DocumentService.GetDocuments()
				.GetModules<ModuleDef>());
			index_cache.RemoveMissing(loaded_modules);
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
			tab.Show(new CallGraphDocumentTabContent(document_tab_service, decompiler_service, settings, theme_service, index_cache, reference), null,
				_ => tab.TrySetFocus());
		}
	}
}
