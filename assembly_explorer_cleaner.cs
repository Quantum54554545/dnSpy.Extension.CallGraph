using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;

namespace dnSpy.CallGraph {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class assembly_explorer_cleaner : IAppCommandLineArgsHandler {
		readonly call_graph_settings settings;
		readonly IDocumentTabService document_tab_service;
		bool initial_arguments_seen;

		[ImportingConstructor]
		assembly_explorer_cleaner(call_graph_settings settings, IDocumentTabService document_tab_service) {
			this.settings = settings;
			this.document_tab_service = document_tab_service;
		}

		public double Order => -2000;

		public void OnNewArgs(IAppCommandLineArgs args) {
			if (this.initial_arguments_seen)
				return;
			this.initial_arguments_seen = true;
			if (!this.settings.clean_assembly_explorer_on_launch)
				return;

			var document_service = this.document_tab_service.DocumentTreeView.DocumentService;
			var command_line_keys = new HashSet<FilenameKey>(
				args.Filenames.Select(filename => new FilenameKey(filename)));

			this.document_tab_service.CloseAll();
			if (command_line_keys.Count == 0) {
				document_service.Clear();
				return;
			}

			var to_remove = document_service.GetDocuments()
				.Where(document => document.Key is not FilenameKey key || !command_line_keys.Contains(key))
				.ToArray();
			if (to_remove.Length != 0)
				document_service.Remove(to_remove);
		}
	}
}
