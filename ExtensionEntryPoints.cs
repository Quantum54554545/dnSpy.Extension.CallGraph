using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.TreeView;

namespace dnSpy.CallGraph {
	[ExportAutoLoaded]
	sealed class CallGraphCommandLoader : IAutoLoaded {
		public static readonly RoutedCommand OpenCommand = new RoutedCommand("OpenCallGraph", typeof(CallGraphCommandLoader));
		readonly IAppWindow app_window;
		readonly CallGraphSettings settings;
		KeyBinding? shortcut_binding;

		[ImportingConstructor]
		CallGraphCommandLoader(IWpfCommandService wpf_command_service, IAppWindow app_window,
			CallGraphSettings settings, Lazy<ICallGraphService> service) {
			this.app_window = app_window;
			this.settings = settings;
			var commands = wpf_command_service.GetCommands(ControlConstants.GUID_MAINWINDOW);
			commands.Add(OpenCommand, new RelayCommand(_ => service.Value.ShowCurrentSelection()));
			settings.Changed += Settings_Changed;
			UpdateShortcut();
		}

		void Settings_Changed(object? sender, EventArgs e) => UpdateShortcut();

		void UpdateShortcut() {
			if (shortcut_binding is not null) {
				app_window.MainWindow.InputBindings.Remove(shortcut_binding);
				shortcut_binding = null;
			}
			if (!settings.ShortcutEnabled)
				return;
			shortcut_binding = new KeyBinding(OpenCommand, settings.ShortcutKey, settings.ShortcutModifiers);
			app_window.MainWindow.InputBindings.Add(shortcut_binding);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "Call Graph",
		Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 2050)]
	sealed class ShowCallGraphMenuItem : MenuItemCommand {
		readonly CallGraphSettings settings;

		[ImportingConstructor]
		ShowCallGraphMenuItem(CallGraphSettings settings) : base(CallGraphCommandLoader.OpenCommand) => this.settings = settings;

		public override string? GetHeader(IMenuItemContext context) => UiText.CallGraph;
		public override string? GetInputGestureText(IMenuItemContext context) => settings.ShortcutDisplayText;
	}

	[ExportMenuItem(Header = "Show in Call Graph", Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 5)]
	sealed class ShowTreeMethodInCallGraphMenuItem : MenuItemBase {
		readonly ICallGraphService service;

		[ImportingConstructor]
		ShowTreeMethodInCallGraphMenuItem(ICallGraphService service) => this.service = service;

		static object? GetReference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return null;
			var nodes = context.Find<TreeNodeData[]>();
			return nodes?.OfType<IMDTokenNode>()
				.Select(node => CallGraphControl.ResolveMethod(node.Reference))
				.FirstOrDefault(reference => reference is not null);
		}

		public override bool IsVisible(IMenuItemContext context) => GetReference(context) is not null;
		public override string? GetHeader(IMenuItemContext context) => UiText.ShowInCallGraph;

		public override void Execute(IMenuItemContext context) {
			if (GetReference(context) is MethodDef method)
				service.Show(method);
		}
	}

	[ExportMenuItem(Header = "Show in Call Graph", Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 5)]
	sealed class ShowCodeMethodInCallGraphMenuItem : MenuItemBase {
		readonly ICallGraphService service;

		[ImportingConstructor]
		ShowCodeMethodInCallGraphMenuItem(ICallGraphService service) => this.service = service;

		static object? GetReference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID))
				return null;
			var reference = context.Find<TextReference>()?.Reference;
			return CallGraphControl.ResolveMethod(reference);
		}

		public override bool IsVisible(IMenuItemContext context) => GetReference(context) is not null;
		public override string? GetHeader(IMenuItemContext context) => UiText.ShowInCallGraph;

		public override void Execute(IMenuItemContext context) {
			if (GetReference(context) is MethodDef method)
				service.Show(method);
		}
	}
}
