using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_reliability() { program.check_bounded_snippets(); program.check_shared_indexes(); program.check_read_isolation(); program.check_decompiler_cancellation(); }
		static void check_bounded_snippets() {
			var cache = new bounded_snippet_cache<int>(3, 20);
			cache[1] = highlighted_snippet.plain("aaaaa"); cache[2] = highlighted_snippet.plain("bbbbb"); cache[3] = highlighted_snippet.plain("ccccc"); cache.try_get_value(1, out _); cache[4] = highlighted_snippet.plain("ddddd");
			program.check("preview cache evicts least recently read entries without removing live graph data", cache.count == 3 && cache.contains_key(1) && !cache.contains_key(2) && cache.character_count == 15);
			cache[1] = highlighted_snippet.plain("123456789012345"); program.check("preview cache enforces character budget after replacement", cache.count == 2 && cache.character_count == 20 && cache.contains_key(1) && cache.contains_key(4));
			cache.remove(4); cache.remove(4); program.check("preview invalidation keeps exact accounting", cache.count == 1 && cache.character_count == 15);
			cache.clear(); program.check("clearing a preview cache releases LRU keys and accounting", cache.count == 0 && cache.character_count == 0 && !cache.keys.Any());
			for (int index = 0; index < 3000; index++) cache[index] = highlighted_snippet.plain("0123456789"); program.check("long preview browsing stays bounded", cache.count == 2 && cache.character_count == 20);
			cache[3001] = highlighted_snippet.plain(new string('x', 100)); program.check("oversized previews remain cacheable instead of causing an endless decompilation retry", cache.count == 1 && cache.character_count == 20 && cache.try_get_value(3001, out var shortened) && shortened.is_truncated && shortened.spans.All(span => span.start + span.length <= shortened.text.Length));
		}
		static void check_shared_indexes() {
			using var input = program.audit_module("IndexReuse"); using var external = program.audit_module("IndexReuseExternal"); var root = program.audit_method(input, "Root"); var target = program.audit_method(external, "Target"); program.audit_call(root, target);
			var cache = new call_graph_index_cache(); var modules = new ModuleDef[] { input, external }; var workspace = cache.get_workspace(modules);
			var first = cache.get_or_build_combined(modules, workspace, default); int scanned = workspace.scanned_method_count; var again = cache.get_or_build_combined(modules, workspace, default);
			program.check("repeated expansion reuses module and combined index instances", ReferenceEquals(first, again) && cache.count == 2 && workspace.scanned_method_count == scanned);
			using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false; try { cache.get_or_build_combined(modules, workspace, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
			program.check("cached index hits still honor cancellation", cancelled);
			var next = program.audit_method(external, "Next"); program.audit_call(target, next); cache.invalidate(external); var updated_workspace = cache.get_workspace(modules); var updated = cache.get_or_build_combined(modules, updated_workspace, default);
			program.check("editing a dependency invalidates reused indexes and reveals the new edge", !ReferenceEquals(first, updated) && updated.all_links_value.Any(link => link.source == target && link.target == next));
			program.check("old index work cannot overwrite a newer workspace generation", !cache.store_combined(new[] { first }, first, workspace) && ReferenceEquals(updated, cache.get_or_build_combined(modules, updated_workspace, default)));
			cache.remove_missing(new HashSet<ModuleDef> { input }); var reduced_workspace = cache.get_workspace(new[] { input }); var reduced = cache.get_or_build_combined(new[] { input }, reduced_workspace, default);
			program.check("unloading a module drops its cached caller index", reduced.modules_value.Count() == 1 && !reduced.all_links_value.Any(link => link.source == target));
		}
		static void check_read_isolation() {
			using var input = program.audit_module("ReadIsolation"); var root = program.audit_method(input, "Root"); var branch = program.audit_method(input, "branch"); var child = program.audit_method(input, "Child"); program.audit_call(root, branch); program.audit_call(branch, child);
			var settings = new call_graph_settings(depth_services.empty<ISettingsService>()); settings.change_graph_depth(1); var cache = new call_graph_index_cache(); string path = Path.Combine(Path.GetTempPath(), "CallGraph-isolation-" + Guid.NewGuid().ToString("N") + ".json");
			MethodDef? opened_method = null; bool opened_new_tab = false; var documents = depth_services.empty<IDocumentTabService>(new Dictionary<string, object?> { ["$callback:FollowReference"] = (Func<object?[]?, object?>)(arguments => { opened_method = arguments![0] as MethodDef; opened_new_tab = arguments[1] is true; return null; }) });
			var control = new call_graph_control(documents, depth_services.empty<IDecompilerService>(), settings, depth_services.empty<IThemeService>(), cache, new research_store(path)); var surface = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(surface, "theme_service", null!);
			void wait_for_work() => program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || (int)program.ui_field(control, "view_workers") != 0 || (bool)program.ui_field(control, "research_reading") || surface.is_rendering);
			control.show_method(root); wait_for_work(); var workspace = cache.get_workspace(new[] { input }); workspace.gate.Wait();
			var handler = typeof(call_graph_control).GetMethod("research_requested", BindingFlags.Instance | BindingFlags.NonPublic)!;
			void request(MethodDef method, research_action action) => handler.Invoke(control, new object?[] { null, new research_action_event_args(method, action) });
			try {
				request(branch, research_action.expand); var expansion = (CancellationTokenSource)program.ui_field(control, "view_cancellation");
				var menu = (ContextMenu)typeof(call_graph_surface).GetMethod("create_node_context_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, new object[] { ((Dictionary<object, graph_node_presentation>)program.ui_field(surface, "node_presentations"))[branch] })!;
				menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, ui_text.open_source)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
				program.check("opening native dnSpy source keeps the selected method and cannot cancel graph expansion", opened_method == branch && opened_new_tab && !expansion.IsCancellationRequested && (int)program.ui_field(control, "view_workers") == 1);
				((Button)control.FindName("close_research_button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); program.check("closing an auxiliary pane does not discard branch expansion", !expansion.IsCancellationRequested);
				request(branch, research_action.evidence); program.check("reading call-site evidence cannot cancel branch expansion", !expansion.IsCancellationRequested);
			} finally { workspace.gate.Release(); }
			wait_for_work(); program.check("expansion survives source navigation and evidence changes and publishes its complete branch", ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Any(node => node.method == child) && control.current_root_value == root);
			((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex = 1; wait_for_work(); var map = (function_map_surface)control.FindName("function_map_surface"); program.pump_ui(() => !map.is_showing(root));
			program.check("evidence-only pane leaves the function-map root intact", control.current_root_value == root);
			((ComboBox)control.FindName("view_mode_combo_box")).SelectedIndex = 0; wait_for_work(); for (int repeat = 0; repeat < 100; repeat++) control.show_method(repeat % 2 == 0 ? branch : child); control.show_method(root); wait_for_work();
			program.check("a burst of 101 root changes cannot publish a cancelled graph over the final selection", control.current_root_value == root && ((call_graph_slice)program.ui_field(control, "research_slice")).nodes.Single(node => node.is_root).method == root && (int)program.ui_field(control, "view_workers") == 0);
			control.dispose_graph(); File.Delete(path);
		}
		static void check_decompiler_cancellation() {
			using var input = program.audit_module("DecompilerCancellation"); var root = program.audit_method(input, "Root"); var target = program.audit_method(input, "Target");
			using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int calls = 0;
			var decompiler = depth_services.empty<IDecompiler>(new Dictionary<string, object?> { ["$get_GenericGuid"] = DecompilerConstants.LANGUAGE_CSHARP, ["$callback:Decompile"] = (Func<object?[]?, object?>)(arguments => {
				if (Interlocked.Increment(ref calls) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decompiler test release missing"); }
				((IDecompilerOutput)arguments![1]!).Write(((MethodDef)arguments[0]!).DeclaringType.Name, TextColor.Text); return null;
			}) });
			highlighted_snippet decompile_preview(MethodDef method, CancellationToken token) => (highlighted_snippet)typeof(call_graph_control).GetMethod("decompile_with_context", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { method, decompiler, 20, token, new DecompilationContext(), 16000, 3000 })!;
			var owner = System.Threading.Tasks.Task.Run(() => decompile_preview(root, default));
			try {
				bool started = entered.Wait(TimeSpan.FromSeconds(2)); program.check("card decompilation runs in its background worker", started && Volatile.Read(ref calls) == 1);
				using var queued_cancellation = new CancellationTokenSource();
				var queued = System.Threading.Tasks.Task.Run(() => {
					try { decompile_preview(target, queued_cancellation.Token); }
					catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException) { return true; }
					return false;
				});
				queued_cancellation.Cancel(); var cancel_wait = Stopwatch.StartNew(); program.pump_ui(() => !queued.IsCompleted && cancel_wait.ElapsedMilliseconds < 300);
				program.check("a cancelled preview stops waiting for an occupied decompiler without waiting for its owner", queued.IsCompletedSuccessfully && queued.Result && !release.IsSet);
				int pulses = 0; var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Input, (_, __) => pulses++, Dispatcher.CurrentDispatcher); var clock = Stopwatch.StartNew(); program.pump_ui(() => clock.ElapsedMilliseconds < 60); timer.Stop(); program.check("UI dispatcher continues servicing input while a card decompiler worker is blocked", pulses > 0);
			} finally { release.Set(); }
			program.pump_ui(() => !owner.IsCompleted); program.check("background card preview returns intact after the occupied decompiler is released", owner.IsCompletedSuccessfully && owner.Result.text == root.DeclaringType.Name && Volatile.Read(ref calls) == 1);
		}
		
		static int stability_audit(string path) {
			var resolver = new AssemblyResolver(); var context = new ModuleContext(resolver); resolver.DefaultModuleContext = context; resolver.PostSearchPaths.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
			using var input = ModuleDefMD.Load(path, context); var workspace = new workspace_analysis(new[] { input });
			var clock = Stopwatch.StartNew(); int methods = 0, calls = 0, addresses = 0, missing = 0;
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
			foreach (var method in input.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)) {
				var slice = workspace.structural_slice(method, 1, int.MaxValue, call_graph_filter_options.none, timeout.Token); var issues = structural_coverage.check(workspace, slice, timeout.Token); missing += issues.Length;
				foreach (var issue in issues.Take(5)) Console.WriteLine($"MISSING token=0x{issue.method.MDToken.Raw:X8} IL_{issue.offset:X4}: {issue.detail}");
				var sites = slice.call_sites.Where(site => site.source == method).ToLookup(site => site.offset);
				foreach (var instruction in method.Body.Instructions) {
					if (program.is_call(instruction)) { calls++; if (!sites[instruction.Offset].Any(site => site.targets.Count != 0 || site.has_unknown_targets)) missing++; }
					if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn) { addresses++; if (!slice.diagnostics_for(method).Any(note => note.offset == instruction.Offset && note.kind == graph_diagnostic_kind.address_reference)) missing++; }
				}
				methods++;
			}
			Console.WriteLine($"AUDIT file={Path.GetFullPath(path)} methodsWithIL={methods} callSites={calls} addressSites={addresses} missing={missing} elapsedMs={clock.ElapsedMilliseconds}");
			Console.WriteLine("Every IL body checked structurally; indirect runtime completeness is not proved. Input was NOT executed."); return missing == 0 && methods != 0 ? 0 : 1;
		}
	}
}
