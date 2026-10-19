using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;

namespace dnSpy.CallGraph {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class AssemblyExplorerCleaner : IAppCommandLineArgsHandler {
		readonly CallGraphSettings settings;
		readonly IDocumentTabService document_tab_service;

		[ImportingConstructor]
		AssemblyExplorerCleaner(CallGraphSettings settings, IDocumentTabService document_tab_service) {
			this.settings = settings;
			this.document_tab_service = document_tab_service;
		}

		
		
		public double Order => -2000;

		public void OnNewArgs(IAppCommandLineArgs args) {
			if (!settings.CleanAssemblyExplorerOnLaunch)
				return;

			var document_service = document_tab_service.DocumentTreeView.DocumentService;
			var command_line_keys = new HashSet<FilenameKey>(
				args.Filenames.Select(filename => new FilenameKey(filename)));

			document_tab_service.CloseAll();
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
