using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph.Controls {
	sealed class file_size_window : MetroWindow {
		sealed record size_location(size_item item, size_item? selected);
		sealed record size_row(size_item item, long total, int depth = 0, bool expanded = false, string location = "") {
			public string name => this.item.name;
			public string size => this.item.size_text;
			public string bytes => this.item.bytes_text;
			public string percent { get { double share = this.total > 0 ? 100.0 * this.item.bytes / this.total : 0; return (share > 0 && share < 0.01 ? "<" + 0.01.ToString("F2") : share.ToString("F2")) + "%"; } }
			public Brush swatch => size_treemap_surface.item_swatch(this.item);
			public string marker => this.item.children.Count == 0 ? "" : this.expanded ? "▾" : "▸";
			public Thickness indent => new Thickness(this.depth * 14, 0, 0, 0);
			public string tooltip => this.location + "\n" + this.item.bytes_text + ui_text.pick(" bytes", " байт") + (this.item.detail.Length == 0 ? "" : "\n" + this.item.detail);
		}
		readonly string[] paths;
		readonly IThemeService theme;
		readonly Func<size_method_reference, bool>? navigate;
		readonly TextBlock summary;
		readonly StackPanel breadcrumb;
		readonly TextBlock status;
		readonly TextBox details;
		readonly ListView tree;
		readonly size_treemap_surface map;
		readonly Button up;
		readonly Button back;
		readonly Button home;
		readonly Button warnings_button;
		readonly Button cancel;
		readonly Button navigate_button;
		readonly ComboBox method_owner;
		readonly HashSet<size_item> expanded = new();
		readonly Dictionary<size_item, size_row> tree_rows = new();
		readonly Stack<size_location> history = new();
		CancellationTokenSource? operation;
		size_inventory? inventory;
		size_item? current;
		size_item? selected;
		string[] warnings = Array.Empty<string>();
		bool closed;
		bool syncing_tree;
		public file_size_window(string[] paths, IThemeService theme, Func<size_method_reference, bool>? navigate = null) {
			this.paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); this.theme = theme; this.navigate = navigate;
			Title = ui_text.pick("File size analysis", "Анализ размера файла"); Width = 1240; Height = 820; MinWidth = 900; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowMinimizeButton = ShowMaximizeButton = true;
			SetResourceReference(StyleProperty, "DialogWindowStyle"); SetResourceReference(BackgroundProperty, "SystemColorsWindow"); SetResourceReference(ForegroundProperty, "SystemColorsWindowText");
			var panel = new DockPanel { Margin = new Thickness(10) };
			var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
			var reload = button(ui_text.pick("Refresh", "Обновить")); DockPanel.SetDock(reload, Dock.Right); toolbar.Children.Add(reload);
			this.cancel = button(ui_text.pick("Cancel", "Отмена")); this.cancel.Visibility = Visibility.Collapsed; DockPanel.SetDock(this.cancel, Dock.Right); toolbar.Children.Add(this.cancel);
			this.warnings_button = button(ui_text.pick("Notes", "Замечания")); this.warnings_button.Visibility = Visibility.Collapsed; DockPanel.SetDock(this.warnings_button, Dock.Right); toolbar.Children.Add(this.warnings_button);
			this.summary = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Text = ui_text.pick("Reading file layout…", "Чтение структуры файла…") }; toolbar.Children.Add(this.summary);
			this.status = new TextBlock { Text = ui_text.pick("Disk size · method header + IL + EH · metadata counted separately", "Размер на диске · метод: заголовок + IL + EH · метаданные учтены отдельно"), Margin = new Thickness(1, 7, 1, 0), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis }; this.status.ToolTip = ui_text.pick("Unsaved edits and virtual memory are not included. Shared bytes are counted once. Unknown data stays in the remainder.", "Несохранённые правки и виртуальная память не учитываются. Общие байты считаются один раз. Неизвестные данные остаются в остатке."); DockPanel.SetDock(this.status, Dock.Bottom); panel.Children.Add(this.status);
			var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.7, GridUnitType.Star), MinHeight = 160 }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star), MinHeight = 150 }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78), MinHeight = 60 }); panel.Children.Add(content);
			var tree_panel = new DockPanel(); content.Children.Add(tree_panel); var tree_bar = new DockPanel { Margin = new Thickness(0, 0, 0, 5) }; DockPanel.SetDock(tree_bar, Dock.Top); tree_panel.Children.Add(tree_bar);
			var expand = button(ui_text.pick("Expand", "Раскрыть")); DockPanel.SetDock(expand, Dock.Right); tree_bar.Children.Add(expand); tree_bar.Children.Add(new TextBlock { Text = ui_text.pick("File structure", "Структура файла"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
			this.tree = table(); tree_panel.Children.Add(this.tree); var columns = (GridView)this.tree.View;
			var name_template = new DataTemplate(); var row_panel = new FrameworkElementFactory(typeof(StackPanel)); row_panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); row_panel.SetBinding(MarginProperty, new Binding("indent") { Mode = BindingMode.OneWay });
			var expander = new FrameworkElementFactory(typeof(TextBlock)); expander.SetValue(WidthProperty, 17.0); expander.SetValue(TagProperty, "size_expander"); expander.SetBinding(TextBlock.TextProperty, new Binding("marker") { Mode = BindingMode.OneWay }); row_panel.AppendChild(expander);
			var swatch = new FrameworkElementFactory(typeof(Border)); swatch.SetValue(WidthProperty, 8.0); swatch.SetValue(HeightProperty, 8.0); swatch.SetValue(MarginProperty, new Thickness(0, 0, 6, 0)); swatch.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); swatch.SetBinding(Border.BackgroundProperty, new Binding("swatch") { Mode = BindingMode.OneWay }); row_panel.AppendChild(swatch);
			var row_name = new FrameworkElementFactory(typeof(TextBlock)); row_name.SetBinding(TextBlock.TextProperty, new Binding("name") { Mode = BindingMode.OneWay }); row_name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); row_panel.AppendChild(row_name); name_template.VisualTree = row_panel;
			columns.Columns.Add(new GridViewColumn { Header = ui_text.pick("Name", "Имя"), Width = 550, CellTemplate = name_template }); column(columns, ui_text.pick("Size", "Размер"), "size", 95); column(columns, ui_text.pick("% of file", "% файла"), "percent", 85); column(columns, ui_text.pick("Bytes", "Байты"), "bytes", 125);
			this.tree.SizeChanged += (_, __) => columns.Columns[0].Width = Math.Max(220, this.tree.ActualWidth - 335);
			var item_style = new Style(typeof(ListViewItem), TryFindResource(typeof(ListViewItem)) as Style); item_style.Setters.Add(new Setter(ToolTipProperty, new Binding("tooltip") { Mode = BindingMode.OneWay })); this.tree.ItemContainerStyle = item_style;
			var horizontal_split = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Height = 5 }; Grid.SetRow(horizontal_split, 1); content.Children.Add(horizontal_split);
			var map_panel = new DockPanel(); Grid.SetRow(map_panel, 2); content.Children.Add(map_panel); var map_bar = new DockPanel { Margin = new Thickness(0, 0, 0, 7) }; DockPanel.SetDock(map_bar, Dock.Top); panel.Children.Insert(panel.Children.IndexOf(content), map_bar);
			this.back = button(ui_text.pick("Back", "Назад")); this.back.ToolTip = "Alt+←"; map_bar.Children.Add(this.back); this.up = button(ui_text.pick("Up", "Выше")); this.up.ToolTip = "Backspace"; map_bar.Children.Add(this.up); this.home = button(ui_text.pick("Whole file", "Весь файл")); map_bar.Children.Add(this.home);
			var legend = new TextBlock { Text = ui_text.pick("Color: item · area: bytes", "Цвет — элемент · площадь — байты"), FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) }; DockPanel.SetDock(legend, Dock.Right); map_bar.Children.Add(legend);
			this.breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 4, 0) }; map_bar.Children.Add(new ScrollViewer { Content = this.breadcrumb, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
			this.map = new size_treemap_surface { describe_item = item => this.inventory?.path(item) ?? item.name }; map_panel.Children.Add(this.map);
			var footer = new DockPanel { Margin = new Thickness(0, 7, 0, 0) }; Grid.SetRow(footer, 3); content.Children.Add(footer);
			var actions = new StackPanel { Width = 310, Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
			this.navigate_button = button(ui_text.pick("Go to method", "Перейти к методу")); this.navigate_button.IsEnabled = false; this.navigate_button.HorizontalAlignment = HorizontalAlignment.Right; actions.Children.Add(this.navigate_button);
			this.method_owner = new ComboBox { DisplayMemberPath = "title", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 5, 0), ToolTip = ui_text.pick("Owner of a shared physical body", "Владелец общего физического тела") }; actions.Children.Add(this.method_owner);
			this.details = new TextBox { IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 11, Padding = new Thickness(3) }; footer.Children.Add(this.details); Content = panel;
			expand.Click += (_, __) => { if (this.tree.SelectedItem is size_row row) this.toggle(row); };
			this.tree.PreviewMouseLeftButtonDown += (_, args) => { if (args.OriginalSource is TextBlock { Tag: "size_expander", DataContext: size_row row }) { this.toggle(row); args.Handled = true; } };
			this.tree.SelectionChanged += (_, __) => { if (this.tree.SelectedItem is size_row row && !this.syncing_tree) { this.focus_scope(row.item.children.Count != 0 ? row.item : this.inventory?.parent_of(row.item) ?? row.item); this.select(row.item); } };
			this.tree.MouseDoubleClick += (_, args) => { if (row_source(this.tree, args) && this.tree.SelectedItem is size_row row) { if (row.item.children.Count != 0) this.toggle(row); else this.go_to_method(); args.Handled = true; } };
			this.tree.PreviewKeyDown += (_, args) => { if (this.tree.SelectedItem is not size_row row) return; if (args.Key == Key.Right && row.item.children.Count > 0 && !this.expanded.Contains(row.item) || args.Key == Key.Left && this.expanded.Contains(row.item)) { this.toggle(row); args.Handled = true; } else if (args.Key == Key.Enter) { this.go_to_method(); args.Handled = true; } };
			this.back.Click += (_, __) => this.go_back(); this.up.Click += (_, __) => this.go_up(); this.home.Click += (_, __) => { if (this.inventory is not null) this.navigate_scope(this.inventory.root); };
			PreviewKeyDown += (_, args) => { if (args.Key == Key.System && args.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { this.go_back(); args.Handled = true; } else if (args.Key == Key.Back && Keyboard.FocusedElement is not TextBox) { this.go_up(); args.Handled = true; } else if (args.Key == Key.Enter && this.map.IsKeyboardFocusWithin) { this.open_selection(); args.Handled = true; } };
			reload.Click += async (_, __) => await this.scan(); this.cancel.Click += (_, __) => this.operation?.Cancel(); this.navigate_button.Click += (_, __) => this.go_to_method();
			this.warnings_button.Click += (_, __) => this.details.Text = string.Join("\n\n", this.warnings);
			this.map.selected_item += item => { this.select(item); this.reveal(item); }; this.map.opened_item += item => { this.select(item); this.open_selection(); };
			this.theme.ThemeChanged += this.theme_changed; Loaded += async (_, __) => await this.scan();
			Closed += (_, __) => { this.closed = true; this.operation?.Cancel(); this.operation?.Dispose(); this.map.close(); this.theme.ThemeChanged -= this.theme_changed; };
		}
		static bool row_source(ListView list, MouseButtonEventArgs args) => args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListViewItem;
		static ListView table() { var list = new ListView { SelectionMode = SelectionMode.Single, View = new GridView() }; VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true); return list; }
		static void column(GridView view, string title, string binding, double width) => view.Columns.Add(new GridViewColumn { Header = title, DisplayMemberBinding = new Binding(binding) { Mode = BindingMode.OneWay }, Width = width });
		static Button button(string title) => new Button { Content = title, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
		void theme_changed(object? sender, ThemeChangedEventArgs args) { if (Dispatcher.CheckAccess()) this.map.refresh_theme(); else Dispatcher.BeginInvoke(new Action(() => { if (!this.closed) this.map.refresh_theme(); })); }
		async Task scan() {
			this.operation?.Cancel(); this.operation?.Dispose(); this.operation = new CancellationTokenSource(); var token = this.operation.Token; this.cancel.Visibility = Visibility.Visible;
			this.status.Text = ui_text.pick("Reading file…", "Чтение файла…");
			try {
				var result = await Task.Run(() => { var reports = this.paths.Select(path => file_size_analysis.analyze(path, token)).ToArray(); var root = reports.Length == 1 ? reports[0].root : size_item.collection(ui_text.pick("Assembly files", "Файлы сборки"), reports.Select(report => report.root)); return (reports, inventory: new size_inventory(root, token)); }, token);
				if (token.IsCancellationRequested || this.closed) return;
				this.inventory = result.inventory; this.warnings = result.reports.SelectMany(report => report.warnings.Select(warning => Path.GetFileName(report.path) + ": " + warning)).ToArray(); this.history.Clear(); this.expanded.Clear(); this.expanded.Add(this.inventory.root);
				this.selected = null; this.show_tree(); this.display(this.inventory.root); this.reveal(this.inventory.root); this.select(this.inventory.root);
				this.summary.Text = this.inventory.root.name + "  ·  " + this.inventory.root.size_text + "  ·  " + this.inventory.root.bytes_text + ui_text.pick(" bytes", " байт"); this.summary.ToolTip = string.Join("\n", this.paths);
				this.status.Text = ui_text.pick($"{this.inventory.methods.Count:N0} physical method bodies · shared bytes counted once · hover for path and size; double-click a method to open code", $"Физических тел методов: {this.inventory.methods.Count:N0} · общие байты учтены один раз · наведение: путь и размер; двойной щелчок по методу: код");
				this.warnings_button.Content = ui_text.pick("Notes", "Замечания") + " · " + this.warnings.Length; this.warnings_button.Visibility = this.warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
			} catch (OperationCanceledException) { if (!this.closed && this.operation?.Token == token) this.status.Text = ui_text.pick("Cancelled. Previous result retained.", "Отменено. Предыдущий результат сохранён."); }
			catch (Exception error) { if (!this.closed && this.operation?.Token == token) this.status.Text = ui_text.pick("Could not read file: ", "Не удалось прочитать файл: ") + error.Message; }
			finally { if (!this.closed && this.operation?.Token == token) this.cancel.Visibility = Visibility.Collapsed; }
		}
		void show_tree() {
			if (this.inventory is null) return; var rows = new List<size_row>(); this.tree_rows.Clear();
			void append(size_item item, int depth) { var row = new size_row(item, this.inventory.root.bytes, depth, this.expanded.Contains(item), this.inventory.path(item)); rows.Add(row); this.tree_rows[item] = row; if (this.expanded.Contains(item)) foreach (var child in item.children) append(child, depth + 1); }
			append(this.inventory.root, 0); this.tree.ItemsSource = rows;
		}
		void toggle(size_row row) { if (row.item.children.Count == 0) return; if (!this.expanded.Remove(row.item)) this.expanded.Add(row.item); this.show_tree(); this.tree.SelectedItem = this.tree.Items.Cast<size_row>().FirstOrDefault(item => item.item == row.item); }
		void reveal(size_item item) {
			if (this.inventory is null) return;
			this.syncing_tree = true;
			try { bool changed = this.tree_rows.Count == 0; foreach (var parent in this.inventory.ancestors(item)) changed |= this.expanded.Add(parent); if (changed) this.show_tree(); if (this.tree_rows.TryGetValue(item, out var row)) { this.tree.SelectedItem = row; this.tree.ScrollIntoView(row); } }
			finally { this.syncing_tree = false; }
		}
		void focus_scope(size_item item) { if (item == this.current) return; if (this.current is not null) this.history.Push(new size_location(this.current, this.selected)); this.display(item); }
		void navigate_scope(size_item item) { this.focus_scope(item); this.reveal(item); this.select(item); }
		void go_back() {
			if (!this.history.TryPop(out var location)) return; this.display(location.item); var item = location.selected ?? location.item; this.reveal(item); this.select(item);
		}
		void go_up() { if (this.current is not null && this.inventory?.parent_of(this.current) is size_item parent) this.navigate_scope(parent); }
		void open_selection() { if (this.selected is not size_item item) return; if (item.method_references.Count != 0) this.go_to_method(); else if (item.children.Count != 0) this.navigate_scope(item); }
		void display(size_item item) {
			this.current = item; this.map.show(item); this.up.IsEnabled = this.inventory?.parent_of(item) is not null; this.back.IsEnabled = this.history.Count != 0; this.home.IsEnabled = this.inventory?.root != item;
			this.breadcrumb.Children.Clear();
			foreach (var entry in (this.inventory?.ancestors(item).Reverse() ?? Enumerable.Empty<size_item>()).Append(item)) {
				if (this.breadcrumb.Children.Count != 0) this.breadcrumb.Children.Add(new TextBlock { Text = " › ", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
				if (entry == item) { this.breadcrumb.Children.Add(new TextBlock { Text = entry.name, MaxWidth = 180, Margin = new Thickness(4, 2, 4, 2), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = this.inventory?.path(entry) ?? entry.name }); continue; }
				var crumb = button(entry.name); crumb.MaxWidth = 180; crumb.Padding = new Thickness(4, 2, 4, 2); crumb.Margin = new Thickness(0); crumb.BorderThickness = new Thickness(0); crumb.Content = new TextBlock { Text = entry.name, TextTrimming = TextTrimming.CharacterEllipsis }; crumb.ToolTip = this.inventory?.path(entry) ?? entry.name; crumb.Click += (_, __) => this.navigate_scope(entry); this.breadcrumb.Children.Add(crumb);
			}
			this.breadcrumb.ToolTip = (this.inventory?.path(item) ?? item.name) + " · " + item.size_text;
		}
		void select(size_item item) {
			this.selected = item;
			this.map.select(item == this.current ? null : item); this.method_owner.ItemsSource = item.method_references; this.method_owner.SelectedIndex = item.method_references.Count == 0 ? -1 : 0; this.method_owner.Visibility = item.method_references.Count > 1 ? Visibility.Visible : Visibility.Collapsed; this.navigate_button.IsEnabled = this.navigate is not null && item.method_references.Count != 0;
			string range = string.Join("  ·  ", item.ranges.Take(4).Select(span => $"0x{span.offset:X8}–0x{span.end:X8}"));
			this.details.Text = (this.inventory?.path(item) ?? item.name) + "\n" + item.bytes_text + " B  ·  " + item.size_text + (this.inventory?.root.bytes > 0 ? "  ·  " + (100.0 * item.bytes / this.inventory.root.bytes).ToString("F2") + ui_text.pick("% of file", "% файла") : "") + (this.current?.bytes > 0 && this.current != this.inventory?.root ? "  ·  " + (100.0 * item.bytes / this.current.bytes).ToString("F2") + ui_text.pick("% of area", "% области") : "") + (range.Length == 0 ? "" : "  ·  " + range) + (item.detail.Length == 0 ? "" : "\n" + item.detail);
		}
		internal void go_to_method() {
			if (this.method_owner.SelectedItem is not size_method_reference reference || this.navigate is null) return;
			if (this.navigate(reference)) WindowState = WindowState.Minimized;
			else this.status.Text = ui_text.pick("The loaded module no longer matches this disk snapshot. Reload the module and refresh the size analysis.", "Загруженный модуль не соответствует снимку на диске. Перезагрузите модуль и обновите анализ размера.");
		}
	}
}
