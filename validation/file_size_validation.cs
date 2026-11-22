using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using dnlib.DotNet;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static IEnumerable<size_item> size_items(size_item item) { yield return item; foreach (var child in item.children) foreach (var nested in size_items(child)) yield return nested; }
		static bool size_accounting(file_size_report report) {
			var items = size_items(report.root).ToArray(); var spans = items.SelectMany(item => item.ranges).OrderBy(span => span.offset).ToArray(); long cursor = 0;
			foreach (var span in spans) { if (span.offset != cursor || span.length <= 0) return false; cursor = span.end; }
			return cursor == report.file_length && report.root.bytes == report.file_length && items.All(item => item.bytes == item.ranges.Sum(span => span.length) + item.children.Sum(child => child.bytes) && (item.children.Count == 0 || item.ranges.Count == 0));
		}
		static int size_audit(string path) {
			var clock = Stopwatch.StartNew(); var report = file_size_analysis.analyze(path); bool valid = size_accounting(report);
			Console.WriteLine($"SIZE file={Path.GetFileName(path)} bytes={report.file_length} counted={report.root.bytes} methods={report.method_count} shared={report.shared_body_count} nodes={size_items(report.root).Count()} warnings={report.warnings.Count} coverage={valid} ms={clock.ElapsedMilliseconds}");
			foreach (var warning in report.warnings.Take(12)) Console.WriteLine("NOTE " + warning);
			return valid ? 0 : 1;
		}
		static void check_file_size() {
			string directory = Path.Combine(Path.GetTempPath(), "CallGraph-size-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			try {
				using var input = program.audit_module("SizeFixture"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second"); program.audit_call(second, first);
				input.Resources.Add(new EmbeddedResource("payload.bin", Enumerable.Range(0, 37).Select(value => (byte)value).ToArray())); string path = Path.Combine(directory, "input.dll"); input.Write(path);
				var report = file_size_analysis.analyze(path); program.check("size analysis partitions every file byte exactly once with additive hierarchy", size_accounting(report));
				program.check("tiny method disk weight includes its one-byte header", size_items(report.root).Any(item => item.kind == ui_text.pick("Method", "Метод") && item.bytes == 2));
				program.check("embedded resource disk weight includes its length prefix", size_items(report.root).Any(item => item.name == "payload.bin" && item.bytes == 41));
				program.check("metadata tables, streams and physical PE sections remain separately attributed", size_items(report.root).Any(item => item.name.StartsWith("MethodDef ·", StringComparison.Ordinal)) && size_items(report.root).Any(item => item.name == "#Strings") && size_items(report.root).Any(item => item.name == ".text"));
				var inventory = new size_inventory(report.root); var method_item = inventory.methods[0];
				program.check("global method inventory is complete, size-ranked and independently reachable from the root", inventory.methods.Count == 2 && inventory.methods[0].bytes >= inventory.methods[1].bytes && inventory.ancestors(method_item).Contains(report.root));
				using (var loaded = ModuleDefMD.Load(path)) {
					var reference = method_item.method_references[0];
					program.check("size navigation resolves the exact disk method in its loaded module", size_inventory.resolve(reference, new[] { loaded }) is MethodDef target && target.MDToken.Raw == reference.token);
					program.check("size navigation rejects changed module identity, path, name and body RVA", size_inventory.resolve(reference with { mvid = Guid.NewGuid() }, new[] { loaded }) is null && size_inventory.resolve(reference with { path = path + ".other" }, new[] { loaded }) is null && size_inventory.resolve(reference with { name = "wrong" }, new[] { loaded }) is null && size_inventory.resolve(reference with { rva = reference.rva + 1 }, new[] { loaded }) is null);
				}
				var original = File.ReadAllBytes(path); var overlay = original.Concat(new byte[123]).ToArray(); string overlay_path = Path.Combine(directory, "overlay.dll"); File.WriteAllBytes(overlay_path, overlay); var overlay_report = file_size_analysis.analyze(overlay_path);
				program.check("overlay bytes are preserved rather than treated as mapped section size", size_accounting(overlay_report) && overlay_report.root.bytes == original.Length + 123 && size_items(overlay_report.root).Any(item => item.name == ui_text.pick("Overlay / outside PE sections", "Оверлей / вне секций PE") && item.bytes == 123));
				using (var pe = new PEReader(new MemoryStream(original))) {
					var reader = pe.GetMetadataReader(); var handles = reader.MethodDefinitions.ToArray(); int row = reader.GetTableRowSize(TableIndex.MethodDef); int start = pe.PEHeaders.MetadataStartOffset + reader.GetTableMetadataOffset(TableIndex.MethodDef);
					var patched = original.ToArray(); BitConverter.GetBytes(reader.GetMethodDefinition(handles[0]).RelativeVirtualAddress).CopyTo(patched, start + row); string shared_path = Path.Combine(directory, "shared.dll"); File.WriteAllBytes(shared_path, patched);
					var shared = file_size_analysis.analyze(shared_path); program.check("duplicate method RVAs are counted once as a shared body, not once per method", size_accounting(shared) && shared.shared_body_count == 1 && size_items(shared.root).Where(item => item.kind == ui_text.pick("Method", "Метод")).Sum(item => item.bytes) == 0 && size_items(shared.root).Any(item => item.name.StartsWith(ui_text.pick("Shared body", "Общее тело"), StringComparison.Ordinal) && item.bytes == 2));
					program.check("shared bodies keep every owner for navigation without duplicating physical weight", new size_inventory(shared.root).methods.Single().method_references.Count == 2 && new size_inventory(shared.root).methods.Single().bytes == 2);
					var certificate = overlay.ToArray(); int directory_start = pe.PEHeaders.PEHeaderStartOffset + (pe.PEHeaders.PEHeader!.Magic == PEMagic.PE32 ? 96 : 112); BitConverter.GetBytes(original.Length).CopyTo(certificate, directory_start + 4 * 8); BitConverter.GetBytes(123).CopyTo(certificate, directory_start + 4 * 8 + 4); string cert_path = Path.Combine(directory, "certificate.dll"); File.WriteAllBytes(cert_path, certificate);
					var cert = file_size_analysis.analyze(cert_path); program.check("certificate directory uses a file offset rather than an RVA", size_accounting(cert) && size_items(cert.root).Any(item => item.name == ui_text.pick("Authenticode certificate table", "Таблица сертификатов Authenticode") && item.bytes == 123));
				}
				foreach (int length in new[] { 0, 1, 63, 128, original.Length / 2 }) { string damaged = Path.Combine(directory, "damaged" + length); File.WriteAllBytes(damaged, original.Take(length).ToArray()); var partial = file_size_analysis.analyze(damaged); program.check("truncated/non-PE input retains exact raw accounting: " + length, size_accounting(partial) && partial.file_length == length && partial.warnings.Count != 0); }
				using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); bool cancelled = false; try { file_size_analysis.analyze(path, cancel.Token); } catch (OperationCanceledException) { cancelled = true; } program.check("file-size scanning honors cancellation before opening a file", cancelled); }
				for (int seed = 0; seed < 30; seed++) {
					var random = new Random(seed); var claims = Enumerable.Range(0, 40).Select(position => { int start = random.Next(300); return new file_size_analysis.claim(start, Math.Min(300, start + random.Next(1, 80)), new[] { new file_size_analysis.part("claim" + position, "claim" + position, "Raw") }, position % 3 == 0); }).ToArray();
					var root = file_size_analysis.partition("ranges", 300, claims, default, new List<string>()); program.check("overlapping/adversarial ranges retain exactly one owner per physical byte: " + seed, size_accounting(new file_size_report(root, "", 300, Array.Empty<string>(), 0, 0)));
				}
				program.check_size_map(report.root);
				program.check_size_scopes(method_item.method_references[0]);
				var window = new file_size_window(new[] { path }, depth_services.empty<IThemeService>(), reference => { return reference == method_item.method_references[0]; }); program.check("size window follows native dnSpy dialog styling", window.GetType().BaseType == typeof(dnSpy.Contracts.Controls.MetroWindow) && window.ReadLocalValue(FrameworkElement.StyleProperty) is not null);
				var scan = (System.Threading.Tasks.Task)typeof(file_size_window).GetMethod("scan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, Array.Empty<object>())!; program.pump_ui(() => !scan.IsCompleted); scan.GetAwaiter().GetResult();
				var initial = (size_inventory)program.ui_field(window, "inventory"); var initial_tree = (ListView)program.ui_field(window, "tree");
				program.check("disk analysis starts with the file selected and only its direct regions expanded", program.ui_field(window, "current") == initial.root && initial_tree.Items.Count == 1 + initial.root.children.Count && initial_tree.SelectedItem!.GetType().GetProperty("item")!.GetValue(initial_tree.SelectedItem) == initial.root && program.ui_field(program.ui_field(window, "map"), "current") == initial.root);
				typeof(file_size_window).GetMethod("select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { method_item }); window.go_to_method();
				program.check("go-to-method button passes the selected structured reference and makes code visible", window.WindowState == WindowState.Minimized);
				window.Close();
			} finally { Directory.Delete(directory, true); }
		}
		static void check_size_scopes(size_method_reference reference) {
			var root = new size_item("File", "File"); var section = root.group("section:0", ".text", "Section"); var bodies = section.group("bodies", "Translated bodies", "IL");
			var global = bodies.group("ns:", "Translated global namespace", "Namespace"); var named = bodies.group("ns:Example", "Example", "Namespace");
			var first_type = global.group("type:1", "SameName", "Type"); var second_type = named.group("type:2", "SameName", "Type");
			var first_method = first_type.group("method:1", "First", "Method", method_references: new[] { reference }); first_method.add(0, 5);
			var second_method = first_type.group("method:2", "Second", "Method", method_references: new[] { reference }); second_method.add(5, 3);
			second_type.group("method:3", "Third", "Method", method_references: new[] { reference }).add(8, 2);
			var shared = bodies.group("shared:1", "Shared", "IL", method_references: new[] { reference, reference }); shared.add(10, 4);
			root.finish(default); var inventory = new size_inventory(root); var bounds = new Rect(0, 0, 900, 600);
			program.check("body hierarchy retains namespaces, identically named classes and separately owned shared bytes", bodies.children.Count == 3 && bodies.children.Contains(global) && bodies.children.Contains(named) && bodies.children.Contains(shared) && first_type.parent == global && second_type.parent == named && inventory.methods.Sum(item => item.bytes) == 14);
			var tiles = size_treemap_layout.build(bodies, bounds);
			program.check("method-body map shows immediate namespace and shared-body children without skipping levels", tiles.Length == 3 && tiles.All(tile => bodies.children.Contains(tile.item)) && tiles.Sum(tile => tile.item.bytes) == bodies.bytes);
			var namespace_tiles = size_treemap_layout.build(global, bounds);
			program.check("single-class namespace does not auto-skip its class level", namespace_tiles.Length == 1 && namespace_tiles[0].item == first_type);
			var type_tiles = size_treemap_layout.build(first_type, bounds);
			program.check("class selection displays all and only its own methods with proportional areas", type_tiles.Length == 2 && type_tiles.All(tile => first_type.children.Contains(tile.item)) && type_tiles.Sum(tile => tile.item.bytes) == 8 && Math.Abs(type_tiles.Single(tile => tile.item == first_method).rectangle.Width * type_tiles.Single(tile => tile.item == first_method).rectangle.Height / (bounds.Width * bounds.Height) - 5.0 / 8) < 0.00001);
			program.check("section selection shows its categories without automatically descending into methods", size_treemap_layout.build(section, bounds).Single().item == bodies);
			program.check("file selection stops at sections even when there is only one section", size_treemap_layout.build(root, bounds).Single().item == section);
			var window = new file_size_window(Array.Empty<string>(), depth_services.empty<IThemeService>()); program.set_ui_field(window, "inventory", inventory);
			void invoke(string name, params object[] arguments) => typeof(file_size_window).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, arguments);
			invoke("show_tree"); invoke("display", root); var tree = (ListView)program.ui_field(window, "tree"); var map = (size_treemap_surface)program.ui_field(window, "map");
			program.check("initial map starts at the file level rather than a recursive method overview", program.ui_field(map, "current") == root);
			void choose(size_item item) { invoke("reveal", item); tree.SelectedItem = null; tree.SelectedItem = tree.Items.Cast<object>().Single(row => ReferenceEquals(row.GetType().GetProperty("item")!.GetValue(row), item)); }
			choose(bodies); var displayed = (size_item)program.ui_field(map, "current");
			program.check("one tree selection immediately scopes the lower map to that heading's children", program.ui_field(window, "current") == bodies && displayed == bodies && displayed.children.SequenceEqual(bodies.children));
			var content = (FrameworkElement)window.Content; content.Measure(new Size(1220, 760)); content.Arrange(new Rect(0, 0, 1220, 760)); content.UpdateLayout();
			program.check("size window has one structure table and no duplicate contents/methods list", size_visuals(content).OfType<ListView>().SequenceEqual(new[] { tree }) && size_visuals(content).OfType<ComboBox>().All(combo => combo == program.ui_field(window, "method_owner")));
			program.check_size_cells(tree);
			choose(global); choose(first_type);
			program.check("namespace and class clicks immediately switch the lower map to their children", program.ui_field(window, "current") == first_type && program.ui_field(map, "current") == first_type);
			invoke("reveal", second_method); program.check("programmatic tree synchronization does not unexpectedly zoom or change history", program.ui_field(window, "current") == first_type);
			((Button)program.ui_field(window, "up")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("up restores the preceding namespace scope and tree selection", program.ui_field(window, "current") == global && tree.SelectedItem!.GetType().GetProperty("item")!.GetValue(tree.SelectedItem) == global);
			((Button)program.ui_field(window, "home")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("whole-file reset restores section level without confusing parent navigation with history", program.ui_field(window, "current") == root && program.ui_field(map, "current") == root && !((Button)program.ui_field(window, "up")).IsEnabled && ((Button)program.ui_field(window, "back")).IsEnabled);
			choose(bodies); invoke("select", first_type); invoke("open_selection");
			program.check("opening a map class keeps every method available without a second table", program.ui_field(window, "current") == first_type && program.ui_field(map, "current") == first_type && first_type.children.Count == 2);
			program.check_size_cells(tree);
			invoke("go_back"); program.check("back restores the prior map scope and selected class", program.ui_field(window, "current") == bodies && program.ui_field(window, "selected") == first_type && program.ui_field(map, "current") == bodies);
			choose(second_type); invoke("go_up"); program.check("up goes to the actual parent namespace rather than the previously visited unrelated class", program.ui_field(window, "current") == named);
			invoke("go_back"); program.check("back remains distinct from parent navigation", program.ui_field(window, "current") == second_type);
			var crumbs = (System.Windows.Controls.StackPanel)program.ui_field(window, "breadcrumb"); var body_crumb = crumbs.Children.OfType<Button>().Single(button => Equals(button.ToolTip, inventory.path(bodies))); body_crumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			program.check("clickable path jumps directly to an ancestor without rescanning the file", program.ui_field(window, "current") == bodies && program.ui_field(window, "inventory") == inventory);
			choose(first_type); invoke("reveal", first_method); var tree_source = tree.ItemsSource; invoke("reveal", second_method);
			program.check("selecting already visible methods reuses tree rows instead of rebuilding and resetting scroll", ReferenceEquals(tree_source, tree.ItemsSource));
			program.check("method selection does not disturb the current class map", program.ui_field(window, "current") == first_type);
			program.check("synthetic Other groups return to their real containing heading without altering ownership", inventory.parent_of(size_item.collection("Other", new[] { shared }, bodies)) == bodies && first_type.children.Sum(item => item.bytes) == first_type.bytes);
			var colors = Enumerable.Range(0, 128).Select(index => new size_item("SameName", "Type", parent: global, key: "type:" + index)).Select(size_treemap_surface.item_color).Distinct().ToArray();
			program.check("class colors use a varied palette instead of one blue category color", colors.Length >= 10);
			program.check("colors use stable node identity rather than translated or obfuscated display labels", size_treemap_surface.item_color(first_type) == size_treemap_surface.item_color(new size_item("Другой заголовок", "Тип", parent: global, key: first_type.key)));
			program.check("tree swatches match map colors and reuse immutable brushes", size_treemap_surface.item_swatch(first_method).Color == size_treemap_surface.item_color(first_method) && size_treemap_surface.item_swatch(first_method).IsFrozen && ReferenceEquals(size_treemap_surface.item_swatch(first_method), size_treemap_surface.item_swatch(first_method)));
			choose(root); invoke("reveal", section); var section_row = tree.Items.Cast<object>().Single(row => row.GetType().GetProperty("item")!.GetValue(row) == section); invoke("toggle", section_row);
			program.check("expanding a section selects its own direct contents without requiring another click", program.ui_field(window, "current") == section && program.ui_field(map, "current") == section);
			section_row = tree.Items.Cast<object>().Single(row => row.GetType().GetProperty("item")!.GetValue(row) == section); invoke("toggle", section_row);
			program.check("collapsing a heading never sends the map down into hidden descendants", program.ui_field(window, "current") == section && program.ui_field(map, "current") == section);
			map.Measure(new Size(900, 600)); map.Arrange(bounds); map.show(root); map.show(bodies); program.pump_ui(() => map.tile_count != 3);
			program.check("changing headings cancels stale layouts and retains exactly the selected child level", map.tile_count == 3 && map.hit(new Point(1, 1)) is size_item hit && bodies.children.Contains(hit));
			window.Close();
		}
		static IEnumerable<DependencyObject> size_visuals(DependencyObject root) { yield return root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in size_visuals(VisualTreeHelper.GetChild(root, index))) yield return child; }
		static void check_size_cells(ListView list) {
			list.Measure(new Size(600, 220)); list.Arrange(new Rect(0, 0, 600, 220)); list.UpdateLayout();
			var containers = Enumerable.Range(0, list.Items.Count).Select(index => list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ListViewItem>().ToArray(); var swatches = containers.SelectMany(size_visuals).OfType<Border>().Where(border => BindingOperations.GetBinding(border, Border.BackgroundProperty)?.Path.Path == "swatch").ToArray();
			program.check("realized structure rows create color swatches without a percentage progress-bar control", containers.Length > 0 && swatches.Length == containers.Length && !containers.SelectMany(size_visuals).OfType<ProgressBar>().Any());
			program.check("realized structure cells read immutable rows through one-way bindings", swatches.Length > 0 && swatches.All(border => BindingOperations.GetBinding(border, Border.BackgroundProperty)?.Mode == BindingMode.OneWay && border.Background is SolidColorBrush brush && border.DataContext is object row && brush.Color == size_treemap_surface.item_color((size_item)row.GetType().GetProperty("item")!.GetValue(row)!)) && ((GridView)list.View).Columns.Skip(1).All(column => column.DisplayMemberBinding is Binding { Mode: BindingMode.OneWay }));
		}
		static void check_size_map(size_item root) {
			var bounds = new Rect(0, 0, 900, 600); var partitions = size_treemap_layout.partition(root.children, bounds, default);
			program.check("treemap rectangle areas preserve file proportions and total area", partitions.Count == root.children.Count && Math.Abs(partitions.Sum(tile => tile.rectangle.Width * tile.rectangle.Height) - bounds.Width * bounds.Height) < 0.001 && partitions.All(tile => Math.Abs(tile.rectangle.Width * tile.rectangle.Height / (bounds.Width * bounds.Height) - (double)tile.item.bytes / root.bytes) < 0.00001));
			program.check("treemap sibling rectangles never overlap", partitions.SelectMany((tile, position) => partitions.Skip(position + 1).Select(other => Rect.Intersect(tile.rectangle, other.rectangle))).All(rect => rect.IsEmpty || rect.Width * rect.Height < 0.001));
			var many = Enumerable.Range(0, 10000).Select(position => { var item = new size_item("Leaf" + position, "Raw"); item.add(position, 1); item.finish(default); return item; }).ToArray(); var huge = size_item.collection("Huge", many); var tiles = size_treemap_layout.build(huge, bounds);
			program.check("tiny treemap items are grouped without dropping their bytes or drill-down access", tiles.Length <= size_treemap_layout.maximum_tiles && tiles.Sum(tile => tile.item.bytes) == huge.bytes && tiles.Any(tile => tile.item.children.Count > 0));
			var nested = size_item.collection("Nested", Enumerable.Range(0, 120).Select(position => size_item.collection("Group" + position, Enumerable.Range(0, 120).Select(child => size_item.collection("Child" + child, many.Take(120))))));
			program.check("nested treemap keeps a strict drawing budget without pruning its source hierarchy", size_treemap_layout.build(nested, new Rect(0, 0, 20000, 20000)).Length <= size_treemap_layout.maximum_tiles && nested.children.Count == 120 && nested.children.All(item => item.children.Count == 120));
			var expanded = size_treemap_layout.build(root, bounds);
			program.check("whole-file treemap contains only immediate physical file regions with complete accounting", expanded.Length == root.children.Count && expanded.All(tile => root.children.Contains(tile.item)) && expanded.Sum(tile => tile.item.bytes) == root.bytes && expanded.All(tile => tile.rectangle.Width > 0 && tile.rectangle.Height > 0));
			program.check("each hierarchy level maps only its direct children with no silent flattening or missing bytes", size_items(root).Where(item => item.bytes > 0).All(item => { var level = size_treemap_layout.build(item, bounds); return level.Sum(tile => tile.item.bytes) == item.bytes && (item.children.Count == 0 ? level.Length == 1 && level[0].item == item : level.Length == item.children.Count && level.All(tile => item.children.Contains(tile.item))); }));
			var surface = new size_treemap_surface { Width = 900, Height = 600 }; surface.Measure(new Size(900, 600)); surface.Arrange(bounds); surface.show(root); surface.show(huge); program.pump_ui(() => surface.tile_count == 0);
			program.check("new size treemap replaces cancelled layouts and remains bounded", surface.tile_count > 0 && surface.tile_count <= size_treemap_layout.maximum_tiles);
			var drawing = new DrawingGroup(); using (var context = drawing.Open()) typeof(size_treemap_surface).GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(surface, new object[] { context });
			program.check("treemap can draw its grouped layout without losing the accounting model", huge.bytes == 10000 && huge.children.Count == 10000); surface.close();
		}
	}
}
