using System;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Themes;
using dnSpy.Contracts.TreeView;

namespace dnSpy.CallGraph {
	[ExportMenuItem(Header = "Analyze file size", Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 6)]
	sealed class file_size_menu : MenuItemBase {
		readonly IAppWindow app;
		readonly IThemeService theme;
		readonly IDocumentTabService tabs;
		[ImportingConstructor]
		public file_size_menu(IAppWindow app, IThemeService theme, IDocumentTabService tabs) { this.app = app; this.theme = theme; this.tabs = tabs; }
		internal static string[] selected_paths(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID)) return Array.Empty<string>();
			var nodes = context.Find<TreeNodeData[]>(); if (nodes?.Length != 1) return Array.Empty<string>();
			return nodes[0] switch {
				AssemblyDocumentNode assembly => new[] { assembly.Document }.Concat(assembly.Document.Children).Select(document => document.Filename).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
				ModuleDocumentNode module => string.IsNullOrWhiteSpace(module.Document.Filename) ? Array.Empty<string>() : new[] { module.Document.Filename },
				_ => Array.Empty<string>(),
			};
		}
		public override string? GetHeader(IMenuItemContext context) => ui_text.pick("Analyze file size…", "Проанализировать размер…");
		public override bool IsVisible(IMenuItemContext context) => selected_paths(context).Length != 0;
		public override void Execute(IMenuItemContext context) {
			var paths = selected_paths(context); if (paths.Length == 0) return;
			bool navigate(size_method_reference reference) {
				var modules = this.tabs.DocumentTreeView.DocumentService.GetDocuments().SelectMany(document => document.NonLoadedDescendantsAndSelf()).Select(document => document.ModuleDef).OfType<dnlib.DotNet.ModuleDef>().Distinct();
				var method = size_inventory.resolve(reference, modules); if (method is null) return false;
				this.tabs.FollowReference(method, newTab: true); this.app.MainWindow.Activate(); return true;
			}
			new file_size_window(paths, this.theme, navigate) { Owner = this.app.MainWindow }.Show();
		}
	}
}
