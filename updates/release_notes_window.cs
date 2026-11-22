using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using dnSpy.Contracts.Controls;

namespace dnSpy.CallGraph.Updates {
	static class release_notes_window {
		internal static string markdown => update_installer.read_resource(ui_text.pick("CallGraph.ReleaseNotes.en.md", "CallGraph.ReleaseNotes.ru.md"));
		public static MetroWindow create(Window owner) {
			var window = release_notes_window.shell(owner, ui_text.pick("What's new in Call Graph", "Что нового в Call Graph"), 680, 540);
			var panel = new DockPanel { Margin = new Thickness(18) };
			var close = new Button { Content = ui_text.pick("Close", "Закрыть"), IsDefault = true, MinWidth = 95, Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
			window.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
			close.Click += (_, __) => window.Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
			var viewer = new FlowDocumentScrollViewer { Document = release_notes_window.render_markdown(release_notes_window.markdown), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
			viewer.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow"); viewer.SetResourceReference(Control.ForegroundProperty, "SystemColorsWindowText");
			panel.Children.Add(viewer); window.Content = panel; return window;
		}
		internal static MetroWindow shell(Window owner, string title, double width, double height) {
			var window = new MetroWindow { Title = title, Width = width, Height = height, MinWidth = 420, MinHeight = 240, Owner = owner, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
			window.SetResourceReference(FrameworkElement.StyleProperty, "DialogWindowStyle");
			window.SetResourceReference(Control.BackgroundProperty, "SystemColorsWindow"); window.SetResourceReference(Control.ForegroundProperty, "SystemColorsWindowText"); return window;
		}
		internal static FlowDocument render_markdown(string markdown) {
			var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, PagePadding = new Thickness(0, 0, 12, 0) };
			document.SetResourceReference(FlowDocument.ForegroundProperty, "SystemColorsWindowText");
			var code = new StringBuilder(); bool fenced = false;
			foreach (string raw in markdown.Replace("\r", "").Split('\n')) {
				string line = raw.TrimEnd();
				if (line.StartsWith("```", StringComparison.Ordinal)) {
					if (fenced) { document.Blocks.Add(new Paragraph(new Run(code.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(10, 6, 0, 10) }); code.Clear(); } fenced = !fenced; continue;
				}
				if (fenced) { code.AppendLine(line); continue; }
				if (line.Length == 0) continue;
				int heading = 0; while (heading < line.Length && heading < 3 && line[heading] == '#') heading++;
				bool title = heading > 0 && line.Length > heading && line[heading] == ' ';
				bool bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
				string content = title ? line.Substring(heading + 1) : bullet ? "•  " + line.Substring(2) : line;
				var paragraph = new Paragraph { Margin = new Thickness(bullet ? 8 : 0, title ? 14 : 2, 0, title ? 8 : 6), FontSize = title ? heading == 1 ? 24 : 16 : 13, FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal };
				int position = 0;
				foreach (Match match in Regex.Matches(content, @"\*\*([^*]+)\*\*|`([^`]+)`")) {
					paragraph.Inlines.Add(new Run(content.Substring(position, match.Index - position)));
					paragraph.Inlines.Add(match.Groups[1].Success ? new Bold(new Run(match.Groups[1].Value)) : new Run(match.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), FontSize = 12 }); position = match.Index + match.Length;
				}
				paragraph.Inlines.Add(new Run(content.Substring(position))); document.Blocks.Add(paragraph);
			}
			if (code.Length != 0) document.Blocks.Add(new Paragraph(new Run(code.ToString())) { FontFamily = new FontFamily("Consolas") });
			return document;
		}
		public static bool confirm(Window owner, update_release release) {
			var window = release_notes_window.shell(owner, ui_text.pick("Update Call Graph", "Обновление Call Graph"), 550, 265);
			window.ResizeMode = ResizeMode.NoResize;
			var panel = new StackPanel { Margin = new Thickness(18) };
			panel.Children.Add(new TextBlock { Text = "Call Graph " + release.display_version, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
			panel.Children.Add(new TextBlock { Text = ui_text.pick("The DLL will be downloaded and checked first. dnSpy will then close normally, install the update and restart. Save your changes and stop debugging before continuing. Cancelling shutdown cancels the installation.", "Сначала DLL будет скачана и проверена. Затем dnSpy завершится обычным способом, установит обновление и запустится снова. Сохраните изменения и остановите отладку перед продолжением. Отмена закрытия отменяет установку."), TextWrapping = TextWrapping.Wrap });
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
			var install = new Button { Content = ui_text.pick("Download and restart", "Скачать и перезапустить"), IsDefault = true, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 0) };
			var cancel = new Button { Content = ui_text.pick("Cancel", "Отмена"), IsCancel = true, MinWidth = 85, Padding = new Thickness(9, 4, 9, 4) };
			install.Click += (_, __) => window.DialogResult = true; buttons.Children.Add(install); buttons.Children.Add(cancel); panel.Children.Add(buttons); window.Content = panel; return window.ShowDialog() == true;
		}
	}
}
