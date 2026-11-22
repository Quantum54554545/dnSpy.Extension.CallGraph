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
	interface i_call_graph_service {
		void show_current_selection();
		void show(MethodDef method);
	}

	[Export(typeof(i_call_graph_service))]
	sealed class call_graph_service : i_call_graph_service {
		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly call_graph_settings settings;
		readonly IThemeService theme_service;
		readonly call_graph_index_cache index_cache = new call_graph_index_cache();
		IDocumentTab? graph_tab;

		[ImportingConstructor]
		public call_graph_service(IDocumentTabService document_tab_service, IDecompilerService decompiler_service,
			call_graph_settings settings, IThemeService theme_service) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
			document_tab_service.DocumentModified += this.document_tab_service_document_modified;
			document_tab_service.DocumentCollectionChanged += this.document_tab_service_document_collection_changed;
		}

		void document_tab_service_document_modified(object? sender, DocumentModifiedEventArgs e) =>
			this.index_cache.invalidate(e.Documents.GetModules<ModuleDef>().Distinct());

		void document_tab_service_document_collection_changed(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			var loaded_modules = new HashSet<ModuleDef>(this.document_tab_service.DocumentTreeView.DocumentService.GetDocuments()
				.GetModules<ModuleDef>());
			this.index_cache.remove_missing(loaded_modules);
		}

		public void show_current_selection() {
			if (this.document_tab_service.ActiveTab?.UIContext is call_graph_document_tab_ui_context context &&
				(context.control.IsKeyboardFocusWithin || context.control.IsMouseOver)) {
				context.control.explore_shortcut();
				return;
			}
			object? reference = call_graph_control.get_current_method(this.document_tab_service);
			this.show_core(reference);
		}

		public void show(MethodDef method) => this.show_core(method);

		void show_core(object? reference) {
			var tab = this.graph_tab;
			if (tab is null || !this.document_tab_service.SortedTabs.Contains(tab)) {
				tab = this.document_tab_service.OpenEmptyTab();
				this.graph_tab = tab;
			}

			this.document_tab_service.ActiveTab = tab;
			tab.Show(new call_graph_document_tab_content(this.document_tab_service, this.decompiler_service, this.settings, this.theme_service, this.index_cache, reference), null,
				_ => tab.TrySetFocus());
		}
	}
}
