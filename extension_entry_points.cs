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
	sealed class call_graph_command_loader : IAutoLoaded {
		public static readonly RoutedCommand open_command = new RoutedCommand("OpenCallGraph", typeof(call_graph_command_loader));
		public const Key shortcut_key = Key.Q;
		public const ModifierKeys shortcut_modifiers = ModifierKeys.Alt;
		public const string shortcut_display_text = "Alt+Q";

		[ImportingConstructor]
		call_graph_command_loader(IWpfCommandService wpf_command_service, IAppWindow app_window,
			Lazy<i_call_graph_service> service) {
			var commands = wpf_command_service.GetCommands(ControlConstants.GUID_MAINWINDOW);
			commands.Add(call_graph_command_loader.open_command, new RelayCommand(_ => service.Value.show_current_selection()));
			app_window.MainWindow.InputBindings.Add(new KeyBinding(call_graph_command_loader.open_command, call_graph_command_loader.shortcut_key, call_graph_command_loader.shortcut_modifiers));
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "Call Graph",
		Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 2050)]
	sealed class show_call_graph_menu_item : MenuItemCommand {
		[ImportingConstructor]
		show_call_graph_menu_item() : base(call_graph_command_loader.open_command) { }

		public override string? GetHeader(IMenuItemContext context) => ui_text.call_graph;
		public override string? GetInputGestureText(IMenuItemContext context) => call_graph_command_loader.shortcut_display_text;
	}

	[ExportMenuItem(Header = "Show in Call Graph", Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 5)]
	sealed class show_tree_method_in_call_graph_menu_item : MenuItemBase {
		readonly i_call_graph_service service;

		[ImportingConstructor]
		show_tree_method_in_call_graph_menu_item(i_call_graph_service service) => this.service = service;

		static object? get_reference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID))
				return null;
			var nodes = context.Find<TreeNodeData[]>();
			return nodes?.OfType<IMDTokenNode>()
				.Select(node => call_graph_control.resolve_method(node.Reference))
				.FirstOrDefault(reference => reference is not null);
		}

		public override bool IsVisible(IMenuItemContext context) => show_tree_method_in_call_graph_menu_item.get_reference(context) is not null;
		public override string? GetHeader(IMenuItemContext context) => ui_text.show_in_call_graph;
		public override string? GetInputGestureText(IMenuItemContext context) => call_graph_command_loader.shortcut_display_text;

		public override void Execute(IMenuItemContext context) {
			if (show_tree_method_in_call_graph_menu_item.get_reference(context) is MethodDef method)
				this.service.show(method);
		}
	}

	[ExportMenuItem(Header = "Show in Call Graph", Icon = DsImagesAttribute.Branch,
		Group = MenuConstants.GROUP_CTX_DOCVIEWER_OTHER, Order = 5)]
	sealed class show_code_method_in_call_graph_menu_item : MenuItemBase {
		readonly i_call_graph_service service;

		[ImportingConstructor]
		show_code_method_in_call_graph_menu_item(i_call_graph_service service) => this.service = service;

		static object? get_reference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID))
				return null;
			var reference = context.Find<TextReference>()?.Reference;
			return call_graph_control.resolve_method(reference);
		}

		public override bool IsVisible(IMenuItemContext context) => show_code_method_in_call_graph_menu_item.get_reference(context) is not null;
		public override string? GetHeader(IMenuItemContext context) => ui_text.show_in_call_graph;
		public override string? GetInputGestureText(IMenuItemContext context) => call_graph_command_loader.shortcut_display_text;

		public override void Execute(IMenuItemContext context) {
			if (show_code_method_in_call_graph_menu_item.get_reference(context) is MethodDef method)
				this.service.show(method);
		}
	}
}
