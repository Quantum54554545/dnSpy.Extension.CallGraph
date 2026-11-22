using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.CallGraph;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Themes;

namespace CallGraph.Validation {
	static partial class program {
		static void check_directions() {
			program.check_lazy_incoming();
			using var input = program.audit_module("DirectionalCalls");
			var root = program.audit_method(input, "Root");
			var target = program.audit_method(input, "Target");
			var wrapper = program.audit_method(input, "Wrapper");
			wrapper.MethodSig = MethodSig.CreateStatic(input.CorLibTypes.Void, input.CorLibTypes.IntPtr);
			wrapper.Body.Instructions.Clear();
			wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
			wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void)));
			wrapper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			root.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldftn, target));
			program.audit_call(root, wrapper);
			var workspace = new workspace_analysis(new[] { input });
			var index = call_graph_index.build(input, default, workspace);
			var forward = workspace.browse(root, 120, call_graph_filter_options.none, default, depth: 3).slice;
			program.check("direction fixture resolves a function pointer passed through an argument", forward.links.Any(link => link.source == wrapper && link.target == target));
			var incoming = index.select(target, call_graph_direction.callers, 3, 120, workspace: workspace, structural: true);
			program.check("switching to callers retains targets discovered from caller arguments", incoming.links.Any(link => link.source == wrapper && link.target == target));
			program.check("unchanged directional selections reuse their evidence and topology", ReferenceEquals(incoming, index.select(target, call_graph_direction.callers, 3, 120, workspace: workspace, structural: true)));
			using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); bool honored = false; try { index.select(target, call_graph_direction.callers, 3, 120, cancellation_token: cancelled.Token, workspace: workspace, structural: true); } catch (OperationCanceledException) { honored = true; } program.check("directional selection cache hits still honor cancellation", honored); }
			program.check("cross-context incoming targets remain candidates rather than unconditional proofs", incoming.links.Where(link => link.source == wrapper && link.target == target).All(link => link.evidence.Any(item => item.kind == call_evidence_kind.possible_target)));
			var both = index.select(target, call_graph_direction.both, 3, 120, workspace: workspace, structural: true);
			program.check("both directions retain targets discovered from caller arguments", both.links.Any(link => link.source == wrapper && link.target == target));
			var cold_workspace = new workspace_analysis(new[] { input }); var cold_index = call_graph_index.build(input, default, cold_workspace);
			var cold = cold_workspace.refine_callers(cold_index, target, default).select(target, call_graph_direction.callers, 3, 120, workspace: cold_workspace);
			program.check("cold incoming analysis propagates function-pointer arguments without a prior forward view", cold.links.Any(link => link.source == wrapper && link.target == target && link.op_codes.Contains("calli")));
			var partial_workspace = new workspace_analysis(new[] { input }) { create_refinement_budget = () => new refinement_budget(step_limit: 0) };
			var partial = partial_workspace.browse(root, 120, call_graph_filter_options.none, default, resume_refinement: false).slice;
			int count = partial_workspace.analysis_count;
			partial_workspace.browse(root, 120, call_graph_filter_options.none, default, resume_refinement: false);
			program.check("ordinary view changes reuse partial results instead of increasing refinement budgets", partial.can_continue_refinement && partial_workspace.analysis_count == count);
			var unknown_workspace = new workspace_analysis(new[] { input }); var unknown_index = call_graph_index.build(input, default, unknown_workspace);
			var unknown_incoming = unknown_index.select(wrapper, call_graph_direction.callers, 1, 120, workspace: unknown_workspace, structural: true);
			program.check("incoming call-site evidence retains unresolved calli uncertainty", unknown_incoming.call_sites.Any(site => site.source == wrapper && site.operation == "calli" && site.has_unknown_targets));
			program.check("caller presentation does not draw outgoing unknown targets as incoming calls", !call_graph_presentation.build(unknown_incoming, new Dictionary<MethodDef, highlighted_snippet>()).graph.nodes.Any(node => node.reference is unresolved_call_target));
			var unknown_forward = workspace.browse(wrapper, 120, call_graph_filter_options.none, default, depth: 1).slice;
			program.check("a known contextual candidate does not erase the unknown pointer remainder", unknown_forward.call_sites.Any(site => site.source == wrapper && site.operation == "calli" && site.targets.Contains(target) && site.has_unknown_targets));
			var snapshot = workspace.include_discoveries(index, default);
			workspace.browse(wrapper, 120, call_graph_filter_options.none, default, depth: 1, resume_refinement: false);
			program.check("cached directional queries do not keep growing duplicate context evidence", ReferenceEquals(snapshot, workspace.include_discoveries(index, default)));
			var revisited = workspace.browse(root, 120, call_graph_filter_options.none, default, depth: 3, resume_refinement: false).slice;
			program.check("shared contextual candidates do not downgrade locally resolved call-site evidence", call_graph_presentation.build(revisited, new Dictionary<MethodDef, highlighted_snippet>()).graph.links.Any(link => link.source == wrapper && Equals(link.target, target) && link.kind == graph_link_kind.call));
			using var bounded = program.audit_module("BoundedDynamicTargets");
			var bounded_root = program.audit_method(bounded, "Root"); var first = program.audit_method(bounded, "First"); var second = program.audit_method(bounded, "Second");
			program.audit_call(bounded_root, first);
			bounded_root.Body.Instructions.Insert(bounded_root.Body.Instructions.Count - 1, Instruction.Create(OpCodes.Ldftn, second));
			bounded_root.Body.Instructions.Insert(bounded_root.Body.Instructions.Count - 1, Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(bounded.CorLibTypes.Void)));
			bounded_root.Body.UpdateInstructionOffsets();
			var bounded_workspace = new workspace_analysis(new[] { bounded }); var bounded_index = call_graph_index.build(bounded, default, bounded_workspace);
			var bounded_view = bounded_workspace.browse(bounded_root, 2, call_graph_filter_options.none, default).slice;
			program.check("a cold bounded view retains resolved targets outside its card limit in the IL model", bounded_view.nodes.Count == 2 && bounded_view.call_sites.Any(site => site.operation == "calli" && site.targets.Contains(second)));
			program.check("reverse lookup retains a dynamic target hidden by the previous view limit", bounded_index.select(second, call_graph_direction.callers, 1, 120, workspace: bounded_workspace).links.Any(link => link.source == bounded_root && link.target == second));
			var choice = program.audit_method(bounded, "Choice"); choice.MethodSig = MethodSig.CreateStatic(bounded.CorLibTypes.Void, bounded.CorLibTypes.Boolean);
			choice.Body.Instructions.Clear(); var alternative = Instruction.Create(OpCodes.Ldftn, second); var invoke = Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(bounded.CorLibTypes.Void));
			foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brtrue, alternative), Instruction.Create(OpCodes.Ldftn, first), Instruction.Create(OpCodes.Br, invoke), alternative, invoke, Instruction.Create(OpCodes.Ret) }) choice.Body.Instructions.Add(instruction);
			choice.Body.UpdateInstructionOffsets();
			var choice_slice = new workspace_analysis(new[] { bounded }).browse(choice, 120, call_graph_filter_options.none, default).slice;
			program.check("finite ambiguous pointer alternatives remain complete without an invented unknown target", choice_slice.call_sites.Any(site => site.source == choice && site.operation == "calli" && site.targets.ToHashSet().SetEquals(new[] { first, second }) && !site.has_unknown_targets));
			var reflection_workspace = new workspace_analysis(new[] { program.module }); var reflection_index = call_graph_index.build(program.module, default, reflection_workspace);
			var reflected = reflection_workspace.refine_callers(reflection_index, program.fixture("reflection_only_target"), default);
			program.check("cold reverse lookup resolves reflection-only callers without inventing name-based edges", reflected.incoming_value(program.fixture("reflection_only_target")).Any(link => link.source == program.fixture("reflection_only") && link.op_codes.Any(operation => operation.Contains("reflection", StringComparison.Ordinal))) && !reflected.incoming_value(program.fixture("reflection_only_target")).Any(link => link.source == program.fixture("reflection_name_only")));
			using var graph = program.audit_module("DirectionalBudget");
			var center = program.audit_method(graph, "Center");
			var caller = program.audit_method(graph, "Caller"); program.audit_call(caller, center);
			var children = Enumerable.Range(0, 5).Select(position => program.audit_method(graph, "Child" + position)).ToArray();
			foreach (var child in children) program.audit_call(center, child);
			program.audit_call(children[0], caller);
			workspace = new workspace_analysis(new[] { graph }); index = call_graph_index.build(graph, default, workspace);
			both = index.select(center, call_graph_direction.both, 1, 4, workspace: workspace, structural: true);
			program.check("both directions reserve space for incoming and outgoing neighbors", both.nodes.Any(node => node.method == caller) && both.nodes.Any(node => children.Contains(node.method)) && both.nodes.Count == 4);
			both = index.select(center, call_graph_direction.both, 1, 20, workspace: workspace, structural: true);
			program.check("both directions preserve cross-links between visible nodes", both.links.Any(link => link.source == children[0] && link.target == caller));
			program.check_direction_mutations();
			using var fan_in = program.audit_module("LargeFanIn"); var sink = program.audit_method(fan_in, "Sink");
			for (int position = 0; position < 5000; position++) program.audit_call(program.audit_method(fan_in, "Caller" + position), sink);
			var fan_workspace = new workspace_analysis(new[] { fan_in }); var fan_index = call_graph_index.build(fan_in, default, fan_workspace);
			var fan_view = fan_index.select(sink, call_graph_direction.callers, 1, 120, workspace: fan_workspace, structural: true);
			program.check("large incoming fan-in retains the complete index with one boundary rather than thousands of duplicate notes", fan_index.incoming_value(sink).Count == 5000 && fan_view.nodes.Count == 120 && fan_view.diagnostics.Count(note => note.kind == graph_diagnostic_kind.node_limit) == 1);
			for (int selected = 0; selected < 80; selected++) fan_index.select(sink, call_graph_direction.callers, 1, 120 + selected, workspace: fan_workspace, structural: true);
			program.check("directional snapshots share a bounded workspace cache without evicting analysis edges", fan_workspace.selections.count <= 32 && fan_workspace.selections.weight_value <= 200000 && fan_index.incoming_value(sink).Count == 5000);
			program.check_direction_invalidation();
			program.check_direction_ui();
		}
		static void check_lazy_incoming() {
			var workspace = new workspace_analysis(new[] { program.module });
			var original = call_graph_index.build(program.module, default, workspace);
			var index = call_graph_index.combine(new[] { original }, workspace: workspace);
			program.check("combining structural indexes shares immutable IL links without eagerly expanding all virtual targets", index.link_count == original.link_count && index.all_links_value.All(original.all_links_value.Contains));
			var sources = new[] { "unknown_virtual", "interface", "unknown_generic_interface", "generic_instance", "invoke_struct", "default_interface", "inherited_interface" }.Select(program.fixture).ToHashSet();
			var expected = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			var sites = sources.SelectMany(source => workspace.call_structure(source, default).calls.Where(instruction => instruction.OpCode.Code == Code.Callvirt)
				.Select(instruction => new virtual_call_site(source, workspace.resolve((IMethod)instruction.Operand)!, instruction.Offset, "possible callvirt target"))).ToArray();
			workspace.resolver_value.add_edges(sites, Array.Empty<reflection_target>(), expected);
			foreach (var source in sources) program.check("eager reference contains a virtual candidate for " + source.Name, expected.Keys.Any(pair => pair.source == source));
			foreach (var target in expected.Keys.Select(pair => pair.target).Distinct()) {
				var view = index.select(target, call_graph_direction.callers, 1, 10000, workspace: workspace, structural: true);
				var actual = view.links.Where(link => sources.Contains(link.source) && link.target == target).ToDictionary(link => link.source);
				program.check("lazy incoming retains every eager candidate and IL offset for " + target.FullName,
					expected.Where(pair => pair.Key.target == target).All(pair => actual.TryGetValue(pair.Key.source, out var link) && pair.Value.offsets.All(link.il_offsets.Contains)));
			}
			var inherited = program.method("inherited_base", "run");
			program.check("reverse interface dispatch includes inherited implementations", workspace.incoming_links(index, inherited, default).Any(link => link.source == program.fixture("inherited_interface")));
			using var alias_module = program.audit_module("AliasedVirtualSlots");
			var contract = new TypeDefUser("Alias", "Contract", null) { Attributes = dnlib.DotNet.TypeAttributes.Interface | dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Public }; alias_module.Types.Add(contract);
			var declaration = new MethodDefUser("Invoke", MethodSig.CreateInstance(alias_module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot | dnlib.DotNet.MethodAttributes.Abstract); contract.Methods.Add(declaration);
			var parent = new TypeDefUser("Alias", "Parent", alias_module.CorLibTypes.Object.TypeDefOrRef); alias_module.Types.Add(parent); parent.Interfaces.Add(new InterfaceImplUser(contract));
			var slot = new MethodDefUser("RenamedSlot", declaration.MethodSig, dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot) { Body = new CilBody() }; parent.Methods.Add(slot); slot.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); slot.Overrides.Add(new MethodOverride(slot, declaration));
			var child = new TypeDefUser("Alias", "Child", parent); alias_module.Types.Add(child);
			var implementation = new MethodDefUser(slot.Name, slot.MethodSig, dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Virtual) { Body = new CilBody() }; child.Methods.Add(implementation); implementation.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var caller = program.audit_method(alias_module, "Caller"); caller.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldnull)); caller.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Callvirt, declaration)); caller.Body.UpdateInstructionOffsets();
			var alias_workspace = new workspace_analysis(new[] { alias_module }); var alias_index = call_graph_index.combine(new[] { call_graph_index.build(alias_module, default, alias_workspace) }, workspace: alias_workspace);
			program.check("renamed inherited MethodImpl slots are candidates in the eager reference", alias_workspace.resolver_value.virtual_targets_value(declaration).Contains(implementation));
			program.check("lazy incoming cannot lose an inherited MethodImpl alias with a different name", alias_index.select(implementation, call_graph_direction.callers, 1, 120, workspace: alias_workspace, structural: true).links.Any(link => link.source == caller && link.target == implementation));
			var writer = program.fixture("initializer");
			var light = workspace.call_structure(writer, default); var full = workspace.structure(writer, default);
			program.check("cheap call indexing cannot substitute for a complete field effect summary", light.writes.Length == 0 && light.has_unknown_effects && full.writes.Length != 0);
			using var first = program.audit_module("StableWorkspace"); using var unrelated = program.audit_module("UnrelatedWorkspace");
			program.audit_method(first, "Root"); program.audit_method(unrelated, "Other");
			var cache = new call_graph_index_cache(); var before = cache.get_workspace(new[] { first });
			cache.get_or_build_combined(new[] { first }, before, default); cache.try_get_value(first, out var saved);
			var after = cache.get_workspace(new[] { first, unrelated }); cache.get_or_build_combined(new[] { first, unrelated }, after, default);
			program.check("loading an unrelated assembly retains valid module indexes", cache.try_get_value(first, out var retained) && ReferenceEquals(saved, retained));
			program.check("workspace growth does not rescan IL bodies of retained modules", after.scanned_method_count == 1);
		}
		static void check_direction_invalidation() {
			using var input = program.audit_module("DirectionalInvalidation");
			var root = program.audit_method(input, "Root"); var old_target = program.audit_method(input, "Old"); var new_target = program.audit_method(input, "New");
			void set_target(MethodDef target) {
				root.Body.Instructions.Clear(); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, target));
				root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void))); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); root.Body.UpdateInstructionOffsets();
			}
			set_target(old_target); var cache = new call_graph_index_cache(); var workspace = cache.get_workspace(new[] { input });
			workspace.browse(root, 120, call_graph_filter_options.none, default); cache.get_or_build_combined(new[] { input }, workspace, default);
			set_target(new_target); cache.invalidate(input); workspace = cache.get_workspace(new[] { input });
			var index = cache.get_or_build_combined(new[] { input }, workspace, default); var refined = workspace.refine_callers(index, new_target, default);
			program.check("editing IL invalidates both directions of discovered pointer targets", refined.incoming_value(new_target).Any(link => link.source == root) && !refined.incoming_value(old_target).Any(link => link.source == root));
		}
		static void check_direction_ui() {
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
			using var input = program.audit_module("DirectionUi");
			var root = program.audit_method(input, "Root"); var caller = program.audit_method(input, "Caller"); var callee = program.audit_method(input, "Callee");
			program.audit_call(caller, root); program.audit_call(root, callee);
			string path = Path.Combine(Path.GetTempPath(), "CallGraph-directions-" + Guid.NewGuid().ToString("N") + ".json");
			var cache = new call_graph_index_cache();
			var control = new call_graph_control(depth_services.empty<IDocumentTabService>(), depth_services.empty<IDecompilerService>(), new call_graph_settings(depth_services.empty<ISettingsService>()), depth_services.empty<IThemeService>(), cache, new research_store(path));
			var blocks = (call_graph_surface)control.FindName("graph_surface"); program.set_ui_field(blocks, "theme_service", null!);
			var map = (function_map_surface)control.FindName("function_map_surface");
			var direction = (ComboBox)control.FindName("direction_combo_box"); var mode = (ComboBox)control.FindName("view_mode_combo_box");
			void settle() { program.pump_ui(() => (bool)program.ui_field(control, "analyzing_graph") || (int)program.ui_field(control, "view_workers") != 0 || blocks.is_rendering); }
			try {
				for (int view = 0; view < 2; view++) {
					mode.SelectedIndex = view;
					for (int selected = 0; selected < 3; selected++) {
						direction.SelectedIndex = selected; control.show_method(root); settle();
						var slice = (call_graph_slice?)program.ui_field(control, "research_slice");
						var expected = selected == 0 ? new[] { root, callee } : selected == 1 ? new[] { root, caller } : new[] { root, caller, callee };
						program.pump_ui(() => view == 1 && ((Dictionary<MethodDef, Point>)program.ui_field(map, "positions")).Count != expected.Length);
						var drawn = view == 0 ? ((Dictionary<object, graph_node_presentation>)program.ui_field(blocks, "node_presentations")).Keys.OfType<MethodDef>() : ((Dictionary<MethodDef, Point>)program.ui_field(map, "positions")).Keys;
						program.check($"WPF view {view} preserves the selected direction {selected} in both model and rendered nodes", slice is not null && slice.direction == (call_graph_direction)selected && slice.nodes.Select(node => node.method).ToHashSet().SetEquals(expected) && drawn.ToHashSet().SetEquals(expected));
					}
				}
				var workspace = cache.get_workspace(new[] { input }); int analyses = workspace.analysis_count;
				workspace.gate.Wait();
				try {
					for (int change = 0; change < 60; change++) { direction.SelectedIndex = change % 3; mode.SelectedIndex = change % 2; }
					mode.SelectedIndex = 0; direction.SelectedIndex = 2;
					int pulses = 0; var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Input, (_, __) => pulses++, Dispatcher.CurrentDispatcher);
					var clock = Stopwatch.StartNew(); program.pump_ui(() => clock.ElapsedMilliseconds < 60); timer.Stop();
					program.check("direction switches keep servicing UI input while the analysis gate is occupied", pulses > 0);
				} finally { workspace.gate.Release(); }
				settle(); var final = (call_graph_slice)program.ui_field(control, "research_slice");
				program.check("rapid direction and view switches cannot publish an older result or restart completed refinement", final.direction == call_graph_direction.both && final.nodes.Count == 3 && workspace.analysis_count == analyses && !(bool)program.ui_field(control, "analyzing_graph"));
			} finally { control.dispose_graph(); if (File.Exists(path)) File.Delete(path); }
		}
		static void check_direction_mutations() {
			for (int seed = 0; seed < 10; seed++) {
				using var input = program.audit_module("DirectionalMutation" + seed);
				var methods = Enumerable.Range(0, 20).Select(position => program.audit_method(input, "λ" + (seed * 31 + position))).ToArray();
				var expected = new HashSet<(MethodDef source, MethodDef target)>(); var random = new Random(seed);
				foreach (var source in methods) for (int call = 0; call < 4; call++) { var target = methods[random.Next(methods.Length)]; program.audit_call(source, target); expected.Add((source, target)); }
				var workspace = new workspace_analysis(new[] { input }); var index = call_graph_index.build(input, default, workspace);
				bool valid = true;
				foreach (var direction in new[] { call_graph_direction.callees, call_graph_direction.callers, call_graph_direction.both }) {
					var slice = index.select(methods[0], direction, 20, 100, workspace: workspace, structural: true);
					var visible = slice.nodes.Select(node => node.method).ToHashSet();
					var actual = slice.links.Select(link => (link.source, link.target)).ToHashSet();
					valid &= expected.Where(edge => visible.Contains(edge.source) && visible.Contains(edge.target)).All(actual.Contains) && actual.IsSubsetOf(expected) && actual.Count == slice.links.Count;
					valid &= slice.links.All(link => index.incoming_value(link.target).Any(reverse => reverse.source == link.source && link.il_offsets.All(reverse.il_offsets.Contains)));
				}
				program.check("all directional views preserve edges and transpose under cycles and renamed metadata, seed " + seed, valid);
			}
		}
		static int direction_audit(string path, params string[] additional_paths) {
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
			using var input = ModuleDefMD.Load(path);
			var additional = additional_paths.Select(file => ModuleDefMD.Load(file)).ToArray();
			var modules = new ModuleDef[] { input }.Concat(additional).ToArray();
			var cache = new call_graph_index_cache(); var workspace = cache.get_workspace(modules);
			var clock = Stopwatch.StartNew();
			var index = additional.Length == 0 ? call_graph_index.build(input, timeout.Token, workspace) : cache.get_or_build_combined(modules, workspace, timeout.Token);
			Console.WriteLine($"INDEX file={Path.GetFileName(path)} methods={index.method_count} links={index.link_count} ms={clock.ElapsedMilliseconds}");
			if (input.EntryPoint is MethodDef entry) {
				clock.Restart(); var cold = workspace.refine_callers(index, entry, timeout.Token);
				long refinement_ms = clock.ElapsedMilliseconds; clock.Restart();
				cold.select(entry, call_graph_direction.callers, 4, 120, cancellation_token: timeout.Token, workspace: workspace);
				Console.WriteLine($"COLD_INCOMING token={entry.MDToken} refineMs={refinement_ms} selectMs={clock.ElapsedMilliseconds}");
			}
			var methods = index.methods.Where(method => method.Module == input && method.HasBody).ToArray();
			var roots = new[] { input.EntryPoint }.OfType<MethodDef>().Concat(index.all_links_value.Where(link => link.target.Module == input).GroupBy(link => link.target).OrderByDescending(group => group.Count()).Take(2).Select(group => group.Key))
				.Concat(methods.Where(method => workspace.call_structure(method, timeout.Token).calls.Any(call => call.OpCode.Code == Code.Calli)).Take(2)).Distinct().Take(5).ToArray();
			foreach (var root in roots) {
				clock.Restart(); var forward = workspace.browse(root, 120, call_graph_filter_options.none, timeout.Token, depth: 4).slice;
				Console.WriteLine($"FORWARD token={root.MDToken} nodes={forward.nodes.Count} links={forward.links.Count} ms={clock.ElapsedMilliseconds}");
				clock.Restart(); var refined = workspace.refine_callers(index, root, timeout.Token);
				int missing = forward.links.Count(link => !refined.incoming_value(link.target).Any(reverse => reverse.source == link.source && link.il_offsets.All(reverse.il_offsets.Contains)));
				Console.WriteLine($"REFINE token={root.MDToken} ms={clock.ElapsedMilliseconds} forwardMissingInReverse={missing}");
				if (missing != 0) return 1;
				foreach (var direction in new[] { call_graph_direction.callers, call_graph_direction.both }) {
					clock.Restart(); var slice = refined.select(root, direction, 4, 120, cancellation_token: timeout.Token, workspace: workspace, forward_slice: direction == call_graph_direction.both ? forward : null);
					Console.WriteLine($"VIEW token={root.MDToken} direction={direction} nodes={slice.nodes.Count} links={slice.links.Count} ms={clock.ElapsedMilliseconds} continuation={slice.can_continue_refinement}");
					var warm = new double[7];
					for (int repeat = 0; repeat < warm.Length; repeat++) { clock.Restart(); refined.select(root, direction, 4, 120, cancellation_token: timeout.Token, workspace: workspace, forward_slice: direction == call_graph_direction.both ? forward : null); warm[repeat] = clock.Elapsed.TotalMilliseconds; }
					Array.Sort(warm); Console.WriteLine($"WARM token={root.MDToken} direction={direction} p50Ms={warm[3]:F3} maxMs={warm[6]:F3}");
				}
			}
			foreach (var module in additional) module.Dispose();
			Console.WriteLine("Input inspected as metadata and IL only; it was NOT executed."); return 0;
		}
	}
}
