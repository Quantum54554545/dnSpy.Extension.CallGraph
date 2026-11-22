using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;

namespace CallGraph.Validation {
	static partial class program {
		sealed class progress_sink : IProgress<call_graph_slice> {
			public readonly List<call_graph_slice> values = new List<call_graph_slice>();
			public void Report(call_graph_slice value) => this.values.Add(value);
		}
		static int failures;
		static int checks;
		static ModuleDefMD module = null!;
		static readonly call_graph_filter_options filter = new call_graph_filter_options(only_current_assembly: true, preserve_structural_calls: false);
		static MethodDef method(string type, string name) => program.module.GetTypes().Single(value => value.FullName == "CallGraph.Validation." + type).Methods.Single(value => value.Name == name);
		static MethodDef fixture(string name) => program.method("fixtures", name);
		static void check(string name, bool condition) { program.checks++; Console.WriteLine((condition ? "PASS " : "FAIL ") + name); if (!condition) program.failures++; }
		static bool has(call_graph_slice slice, MethodDef method) => slice.links.Any(link => ReferenceEquals(link.target, method));
		static call_graph_slice analyze(string name, workspace_analysis? workspace = null) {
			workspace ??= new workspace_analysis(new[] { program.module });
			return workspace.analyze(program.fixture(name), 8, 250, program.filter, null, CancellationToken.None).slice;
		}
		[STAThread]
		static int Main(string[] arguments) {
			try {
				if (arguments.Length == 1 && arguments[0] == "--render-perf") return program.render_performance();
				if (arguments.Length == 2 && arguments[0] == "--size-audit") return program.size_audit(arguments[1]);
				if (arguments.Length == 2 && arguments[0] == "--stability-audit") return program.stability_audit(arguments[1]);
				if (arguments.Length == 2 && arguments[0] == "--direction-audit") return program.direction_audit(arguments[1]);
				if (arguments.Length >= 3 && arguments[0] == "--workspace-audit") return program.direction_audit(arguments[1], arguments.Skip(2).ToArray());
				if (arguments.Length != 0) { Console.Error.WriteLine("Usage: CallGraph.Validation [--render-perf | --size-audit <assembly> | --stability-audit <assembly> | --direction-audit <assembly> | --workspace-audit <assembly> <additional assemblies...>]"); return 2; }
				program.module = ModuleDefMD.Load(Assembly.GetExecutingAssembly().Location);
				var clock = Stopwatch.StartNew();
				var workspace = new workspace_analysis(new[] { program.module }); var progress = new progress_sink();
				var result = workspace.analyze(program.fixture("known_branch"), 8, 250, program.filter, progress, CancellationToken.None);
				program.check("structural graph is published first", progress.values.Count != 0 && progress.values[0].is_structural);
				program.check("structural preview is not replaced by a root-only slice", progress.values.Count == 1);
				program.check("structural graph includes both branch operands", program.has(progress.values[0], program.fixture("leaf")) && program.has(progress.values[0], program.fixture("other_leaf")));
				program.check("refinement removes proven unreachable branch", program.has(result.slice, program.fixture("leaf")) && !program.has(result.slice, program.fixture("other_leaf")));
				int analyses = workspace.analysis_count, scans = workspace.scanned_method_count;
				var cached = workspace.analyze(program.fixture("known_branch"), 8, 250, program.filter, null, CancellationToken.None);
				program.check("query result is cached", ReferenceEquals(result.slice, cached.slice) && analyses == workspace.analysis_count && scans == workspace.scanned_method_count);
				var direct = program.analyze("direct"); var direct_link = direct.links.Single(link => link.source == program.fixture("direct") && link.target == program.fixture("leaf"));
				program.check("distinct IL call sites retain their offsets", direct_link.call_site_count == 2 && direct_link.evidence.Select(item => item.offset).Distinct().Count() == 2);
				program.check("per-site targets are available", direct.call_sites.Count(site => site.source == program.fixture("direct") && site.targets.Contains(program.fixture("leaf"))) == 2);
				var virtual_slice = program.analyze("virtual");
				program.check("virtual receiver chooses override", program.has(virtual_slice, program.method("derived", "run")) && !program.has(virtual_slice, program.method("base", "run")));
				var hidden = program.analyze("new_slot");
				program.check("newslot preserves base slot", program.has(hidden, program.method("base", "run")) && !program.has(hidden, program.method("hidden", "run")));
				var possible = program.analyze("unknown_virtual");
				program.check("unknown receiver preserves virtual slot identity", program.has(possible, program.method("derived", "run")) && !program.has(possible, program.method("hidden_derived", "run")));
				program.check("explicit interface implementation", program.analyze("interface").links.Any(link => link.target.DeclaringType.Name == "explicit" && link.target.Name.ToString().EndsWith(".run", StringComparison.Ordinal)));
				program.check("default interface chooses most-specific implementation", program.analyze("default_interface").links.Any(link => link.target.DeclaringType.Name == "i_default_derived" && link.target.Name.ToString().EndsWith(".run", StringComparison.Ordinal) && !link.is_potential));
				program.check("constructed generic interface", program.has(program.analyze("generic_interface"), program.method("int_value", "go")));
				program.check("generic interface exact receiver is resolved", program.analyze("generic_interface").links.Any(link => link.target == program.method("int_value", "go") && !link.is_potential));
				program.check("constructed generic receiver", program.has(program.analyze("generic_instance"), program.method("generic_value`1", "go")));
				program.check("generic receiver exact dispatch", program.analyze("generic_instance").links.Any(link => link.target == program.method("generic_value`1", "go") && !link.is_potential));
				program.check("unknown generic interface retains implementations", program.has(program.analyze("unknown_generic_interface"), program.method("int_value", "go")));
				program.check("constrained generic struct call", program.has(program.analyze("constrained"), program.method("value", "run")));
				program.check("delegate invocation resolves target", program.has(program.analyze("delegate"), program.fixture("leaf")));
				program.check("reflection resolves target", program.has(program.analyze("reflection"), program.fixture("leaf")));
				program.check("reflection DeclaredOnly excludes inherited methods", !program.analyze("reflection_declared_only").links.Any(link => link.target == program.method("reflection_parent", "inherited_only")));
				program.check("managed function pointer resolves target", program.has(program.analyze("pointer"), program.fixture("leaf")));
				var unknown = program.analyze("unknown_pointer");
				program.check("unresolved calli is explicit and later calls survive", unknown.unresolved_indirect_call_count > 0 && program.has(unknown, program.fixture("leaf")) && unknown.call_sites.Any(site => site.operation == "calli" && site.uncertainty is not null));
				program.check("unknown delegate remains diagnostic", program.analyze("unknown_delegate").diagnostics.Any(item => item.kind == graph_diagnostic_kind.unresolved_delegate));
				var exceptions = program.analyze("exception");
				program.check("catch and finally calls survive", program.has(exceptions, program.fixture("leaf")) && program.has(exceptions, program.fixture("other_leaf")));
				program.check("type initializer is represented", program.has(program.analyze("initializer"), program.method("initialized", ".cctor")));
				program.check("async state machine is represented", program.analyze("async").links.Any(link => link.target.Name == "MoveNext"));
				var effects = workspace.summaries_value.get(program.fixture("recursive_b"), CancellationToken.None);
				program.check("recursive SCC reaches effect fixed point", effects.is_recursive && effects.writes.Any(field => field.Name == "field") && workspace.summaries_value.fixed_point_passes > 1);
				program.check("recursive graph remains finite", program.analyze("unknown_recursion").nodes.Count < 250);
				program.check_cache_invalidation();
				program.check_incremental_dependencies();
				program.check_structural_merge();
				program.check_constrained_operand();
				program.check_partial_analysis();
				program.check_all_il_calls();
				program.check_evidence();
				program.check_generated_graphs();
				program.check_audit_regressions();
				program.check_directions();
				program.check_browse();
				program.check_hybrid();
				program.check_refinement_continuations();
				program.check_depth_selection();
				program.check_research();
				program.check_settings();
				program.check_release_compatibility();
				program.check_compact_layout();
				program.check_render_reliability();
				program.check_reaudit();
				program.check_file_size();
				program.check_updates();
				program.check_reliability();
				program.check_ui(direct);
				Console.WriteLine($"RESULT {program.checks - program.failures}/{program.checks} passed in {clock.ElapsedMilliseconds} ms");
				return program.failures == 0 ? 0 : 1;
			}
			catch (Exception error) { Console.WriteLine(error); return 2; }
		}
		static void check_cache_invalidation() {
			var cache = new call_graph_index_cache(); var first = cache.get_workspace(new[] { program.module });
			var before = first.analyze(program.fixture("direct"), 2, 40, program.filter, null, CancellationToken.None);
			var leaf = program.fixture("leaf"); var original = leaf.Body.Instructions.ToArray();
			try {
				leaf.Body.Instructions.Clear(); leaf.Body.Instructions.Add(Instruction.Create(OpCodes.Call, program.fixture("other_leaf"))); leaf.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); leaf.Body.UpdateInstructionOffsets();
				cache.invalidate(program.module); var next = cache.get_workspace(new[] { program.module });
				var after = next.analyze(program.fixture("direct"), 3, 40, program.filter, null, CancellationToken.None);
				program.check("edit invalidates method and query caches", !ReferenceEquals(first, next) && program.has(after.slice, program.fixture("other_leaf")) && !program.has(before.slice, program.fixture("other_leaf")));
			}
			finally { leaf.Body.Instructions.Clear(); foreach (var instruction in original) leaf.Body.Instructions.Add(instruction); }
		}

		static void check_structural_merge() {
			var workspace = new workspace_analysis(new[] { program.module });
			var index = call_graph_index.build(program.module, default, workspace); int scans = workspace.scanned_method_count;
			var combined = call_graph_index.combine(new[] { index }, default, workspace);
			program.check("combination preserves indexed links", index.all_links_value.All(link => combined.outgoing_value(link.source).Any(other => other.target == link.target)));
			program.check("combination does not rescan method bodies", workspace.scanned_method_count == scans);
			var refined = workspace.refine_index(combined, CancellationToken.None);
			var callers = refined.select(program.fixture("leaf"), call_graph_direction.callers, 3, 250, program.filter, default, workspace);
			program.check("incoming index contains delegate invocation", callers.links.Any(link => link.source == program.fixture("delegate") && link.target == program.fixture("leaf")));
			using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool canceled = false;
			try { program.analyze_canceled(cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
			program.check("cancellation is observed", canceled);
		}
		static void check_incremental_dependencies() {
			ModuleDefUser create_module(string name) {
				var value = new ModuleDefUser(name); new AssemblyDefUser(name, new Version(1, 0)).Modules.Add(value); return value;
			}
			MethodDef create_method(ModuleDef owner, string name) {
				var type = new TypeDefUser("Cache", name, owner.CorLibTypes.Object.TypeDefOrRef); owner.Types.Add(type);
				var method = new MethodDefUser("run", MethodSig.CreateStatic(owner.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
				type.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
			}
			using var first = create_module("First"); using var second = create_module("Second"); using var unrelated = create_module("Unrelated");
			var caller = create_method(first, "Caller"); var target = create_method(second, "Target"); var independent = create_method(unrelated, "Independent");
			caller.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, target)); caller.Body.UpdateInstructionOffsets();
			var cache = new call_graph_index_cache(); var modules = new ModuleDef[] { first, second, unrelated }; var before = cache.get_workspace(modules);
			var dependent = before.summaries_value.get(caller, default); var stable = before.summaries_value.get(independent, default);
			cache.invalidate(second); var after = cache.get_workspace(modules);
			program.check("changed callee invalidates dependent effects", !ReferenceEquals(dependent, after.summaries_value.get(caller, default)));
			program.check("unrelated effect summary survives invalidation", ReferenceEquals(stable, after.summaries_value.get(independent, default)));
			int scans = after.scanned_method_count; after.structure(caller, default);
			program.check("unchanged caller IL snapshot is reused", scans == after.scanned_method_count);
		}
		static void analyze_canceled(CancellationToken token) => new workspace_analysis(new[] { program.module }).analyze(program.fixture("direct"), 3, 40, program.filter, null, token);
		static void check_evidence() {
			var accumulator = new call_site_accumulator(); accumulator.add(3, "call", false, "direct IL operand"); accumulator.add(9, "calli", true, "partial pointer target set");
			var link = workspace_analysis.link(program.fixture("direct"), program.fixture("leaf"), accumulator);
			program.check("mixed certainty stays separate per call site", !link.is_potential && link.evidence.Any(item => item.offset == 9 && item.is_potential) && link.evidence.Any(item => item.offset == 3 && !item.is_potential));
			program.check("evidence identifies its candidate target", link.evidence.All(item => item.target == program.fixture("leaf")));
		}
		static void check_partial_analysis() {
			var method = program.fixture("direct"); var saved = method.Body.Instructions.ToArray();
			try {
				method.Body.Instructions.Clear();
				method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
				method.Body.Instructions.Add(Instruction.Create(OpCodes.Refanytype));
				method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
				method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, program.fixture("other_leaf")));
				method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); method.Body.UpdateInstructionOffsets();
				var slice = program.analyze("direct"); program.check("unsupported flow retains structural calls", program.has(slice, program.fixture("other_leaf")) && slice.diagnostics.Any(item => item.kind == graph_diagnostic_kind.unsupported_instruction));
			}
			finally { method.Body.Instructions.Clear(); foreach (var instruction in saved) method.Body.Instructions.Add(instruction); }
		}
		static void check_all_il_calls() {
			var workspace = new workspace_analysis(new[] { program.module });
			var all = new call_graph_filter_options(only_current_assembly: true);
			call_graph_slice full(string name) => workspace.analyze(program.fixture(name), int.MaxValue, 500, all, null, default).slice;
			var loader = full("after_loader_failure");
			program.check("all-IL graph retains calls after inferred loader exception", program.has(loader, program.fixture("leaf")) && program.has(loader, program.fixture("other_leaf")));
			program.check("retained call is marked possible", loader.links.Any(link => link.source == program.fixture("after_loader_failure") && link.target == program.fixture("leaf") && link.is_potential));
			var crypto = full("crypto_guard");
			program.check("all-IL graph expands through null-state guard", program.has(crypto, program.fixture("leaf")) && program.has(crypto, program.fixture("crypto_inner")) && program.has(crypto, program.fixture("other_leaf")));
			var initialized = full("after_failed_initializer");
			program.check("initializer edge does not hide original call at same offset", program.has(initialized, program.method("failed_initializer", "enter")) && program.has(initialized, program.fixture("leaf")) && program.has(initialized, program.fixture("other_leaf")));
			var branch = full("known_branch");
			program.check("all-IL mode retains both IL branches", program.has(branch, program.fixture("leaf")) && program.has(branch, program.fixture("other_leaf")));
			var contextual = workspace.analyze(program.fixture("known_branch"), int.MaxValue, 500, program.filter, null, default).slice;
			program.check("contextual cache remains separate from all-IL mode", program.has(contextual, program.fixture("leaf")) && !program.has(contextual, program.fixture("other_leaf")));
			var exhausted = new reachable_call_analysis(new[] { program.module }, default);
			typeof(reachable_call_analysis).GetField("remaining_steps", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(exhausted, 0);
			var native = exhausted.select(program.fixture("native_boundary"), int.MaxValue, 500, all);
			program.check("exhausted flow budget handles methods without IL bodies", program.has(native, program.fixture("get_current_process_id")) && program.has(native, program.fixture("other_leaf")));
			foreach (var node in crypto.nodes.Where(node => node.method.HasBody)) {
				foreach (var instruction in node.method.Body.Instructions.Where(instruction => instruction.Operand is IMethod && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp)) {
					var target = workspace.resolve((IMethod)instruction.Operand);
					if (target is null || !all.includes(program.fixture("crypto_guard"), target)) continue;
					program.check("visible IL operand has graph edge at " + node.method.Name + ":" + instruction.Offset, crypto.links.Any(link => link.source == node.method && link.target == target && link.il_offsets.Contains(instruction.Offset)));
				}
			}
		}
		static void check_constrained_operand() {
			var type = new TypeDefUser("CallGraph.Validation", "Synthetic", program.module.CorLibTypes.Object.TypeDefOrRef); program.module.Types.Add(type);
			var method = new MethodDefUser("ConstrainedConcrete", MethodSig.CreateStatic(program.module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody { InitLocals = true } };
			type.Methods.Add(method); var local = new Local(new ValueTypeSig(program.method("value", "run").DeclaringType)); method.Body.Variables.Add(local);
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, local)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Constrained, program.method("value", "run").DeclaringType));
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, program.method("i_run", "run"))); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); method.Body.UpdateInstructionOffsets();
			var slice = new workspace_analysis(new[] { program.module }).analyze(method, 3, 40, program.filter, null, CancellationToken.None).slice;
			program.check("constrained concrete value-type dispatch", program.has(slice, program.method("value", "run")));
		}
		static void check_ui(call_graph_slice slice) {
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
			var snippets = slice.nodes.ToDictionary(node => node.method, _ => highlighted_snippet.plain("call();"));
			var surface = new call_graph_surface(); surface.render(slice, snippets);
			var map = new function_map_surface(); map.render(slice);
			var clock = Stopwatch.StartNew();
			while (clock.ElapsedMilliseconds < 3000 && (surface.is_rendering || !surface.is_showing(slice.nodes.Single(node => node.is_root).method) || !map.is_showing(slice.nodes.Single(node => node.is_root).method))) {
				var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1);
			}
			program.check("WPF surfaces accept background layout", surface.is_showing(slice.nodes.Single(node => node.is_root).method) && map.is_showing(slice.nodes.Single(node => node.is_root).method));
			program.check("background layout completes", !surface.is_rendering);
			var large_nodes = Enumerable.Range(0, 2000).Select(index => new MethodDefUser("Node" + index, MethodSig.CreateStatic(program.module.CorLibTypes.Void))).ToArray();
			var large = new call_graph_slice(large_nodes.Select((method, index) => new call_graph_node(method, index == 0 ? 0 : 1 + index / 100, index == 0, false)).ToArray(),
				large_nodes.Skip(1).Select(method => new call_graph_link(large_nodes[0], method, 1, new uint[] { 0 }, new[] { "call" })).ToArray(), false);
			var large_snippets = large_nodes.ToDictionary(method => (MethodDef)method, _ => highlighted_snippet.plain("line();\nline();\nline();"));
			surface.render(large, large_snippets); surface.cancel_pending_render(); surface.render(slice, snippets);
			clock.Restart();
			while (clock.ElapsedMilliseconds < 3000 && surface.is_rendering) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
			program.check("stale large layout cannot replace a newer graph", surface.is_showing(slice.nodes.Single(node => node.is_root).method) && !surface.is_rendering);
			surface.render(large, large_snippets); clock.Restart();
			while (clock.ElapsedMilliseconds < 5000 && surface.is_rendering) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
			program.check("2000-node graph finishes in UI batches", surface.is_showing(large_nodes[0]) && !surface.is_rendering);
			surface.update_snippets(snippets); surface.cancel_pending_render(); map.cancel_pending_render(); surface.clear(); map.clear();
		}
	}
}
