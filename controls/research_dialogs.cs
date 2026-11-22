using System.Windows;
using System.Windows.Controls;
using dnSpy.Contracts.Controls;

namespace dnSpy.CallGraph.Controls {
	static class research_dialogs {
		public static string? edit(DependencyObject owner, string title, string value, bool multiline) {
			var (window, text) = research_dialogs.create(owner, title, value, multiline);
			return window.ShowDialog() == true ? text.Text : null;
		}
		internal static (MetroWindow window, TextBox editor) create(DependencyObject owner, string title, string value, bool multiline) {
			var window = new MetroWindow { Title = title, Width = 500, Height = multiline ? 330 : 165, MinWidth = 350, MinHeight = multiline ? 220 : 165, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(owner), ShowInTaskbar = false };
			window.SetResourceReference(FrameworkElement.StyleProperty, "DialogWindowStyle");
			var panel = new DockPanel { Margin = new Thickness(12) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
			var save = new Button { Content = ui_text.pick("Save", "Сохранить"), IsDefault = true, MinWidth = 85, Padding = new Thickness(8, 3, 8, 3) };
			var cancel = new Button { Content = ui_text.pick("Cancel", "Отмена"), IsCancel = true, MinWidth = 85, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(7, 0, 0, 0) };
			buttons.Children.Add(save); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
			var text = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, Padding = new Thickness(5), VerticalContentAlignment = VerticalAlignment.Top };
			text.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow"); text.SetResourceReference(Control.ForegroundProperty, "SystemColorsWindowText"); text.SetResourceReference(Control.BorderBrushProperty, "CommonControlsTextBoxBorder");
			panel.Children.Add(text); window.Content = panel; save.Click += (_, __) => window.DialogResult = true; window.Loaded += (_, __) => { text.Focus(); if (!multiline) text.SelectAll(); };
			return (window: window, editor: text);
		}
	}
}
