using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;

namespace CallGraph.Validation {
	static partial class program {
		static void check_refinement_continuations() {
			program.check("empty symbolic merge stays unknown, not non-returning", symbolic_value.merge(Array.Empty<symbolic_value>()).is_unknown);
			program.check("recursive bottom is immutable across value transformations", !ReferenceEquals(symbolic_value.bottom, symbolic_value.bottom.clone()) && symbolic_value.bottom.methods.Count == 0);
			foreach (var item in new[] { ("recursive_delegate", "leaf"), ("unknown_recursive_delegate", "leaf"), ("mutual_delegate", "leaf"), ("recursive_pointer", "leaf"), ("recursive_heap", "leaf") }) {
				var slice = new workspace_analysis(new[] { program.module }).browse(program.fixture(item.Item1), 120, call_graph_filter_options.none, default).slice;
				program.check("recursive return/heap target: " + item.Item1, slice.links.Any(link => link.source == program.fixture(item.Item1) && link.target == program.fixture(item.Item2) && link.op_codes.Any(operation => operation is "calli" or "delegate invoke")));
			}
			var mutual = program.analyze("unknown_mutual_delegate");
			program.check("mutual recursive return fixed point retains both base targets", program.has(mutual, program.fixture("leaf")) && program.has(mutual, program.fixture("other_leaf")));
			var nonreturn = program.analyze("non_returning_delegate");
			program.check("recursive component without base return invents no target", !program.has(nonreturn, program.fixture("leaf")) && !program.has(nonreturn, program.fixture("other_leaf")));
			var scheduled = new workspace_analysis(new[] { program.module }).browse(program.fixture("schedule_via_wrapper"), 120, call_graph_filter_options.none, default).slice;
			program.check("structural frontier optimization preserves scheduled callbacks", scheduled.links.Any(link => link.source == program.fixture("schedule") && link.target == program.fixture("leaf") && link.op_codes.Contains("scheduled callback")));
			var async = new workspace_analysis(new[] { program.module }).browse(program.fixture("async_via_wrapper"), 120, call_graph_filter_options.none, default).slice;
			program.check("structural frontier optimization preserves async state machines", async.links.Any(link => link.source == program.fixture("async") && link.target.Name == "MoveNext" && link.op_codes.Contains("state machine")));
			using var input = program.audit_module("Continuations");
			var root = program.audit_method(input, "Root"); var target = program.audit_method(input, "Target");
			root.Body.Instructions.Clear();
			for (int position = 0; position < 90; position++) root.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
			root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, target));
			root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void)));
			root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); root.Body.UpdateInstructionOffsets();
			var workspace = new workspace_analysis(new[] { input }) { create_refinement_budget = () => new refinement_budget(step_limit: 20) };
			var partial = workspace.browse(root, 120, call_graph_filter_options.none, default).slice;
			program.check("work slice pauses with a resumable frontier", partial.can_continue_refinement && partial.diagnostics.Any(note => note.kind == graph_diagnostic_kind.analysis_budget));
			var complete = workspace.browse(root, 120, call_graph_filter_options.none, default).slice;
			program.check("same cached query continues instead of returning the partial slice", workspace.analysis_count == 2 && !complete.can_continue_refinement && complete.links.Any(link => link.source == root && link.target == target && link.op_codes.Contains("calli")));
			program.check("continued query removes obsolete budget diagnostics", !complete.diagnostics.Any(note => note.kind == graph_diagnostic_kind.analysis_budget));
			program.check("continued pointer resolution removes its temporary unknown card", !call_graph_presentation.build(complete, new Dictionary<MethodDef, highlighted_snippet>()).graph.nodes.Any(node => node.reference is unresolved_call_target));
			program.check("completed query is cached after continuation", ReferenceEquals(complete, workspace.browse(root, 120, call_graph_filter_options.none, default).slice) && workspace.analysis_count == 2);
			var addresses = new workspace_analysis(new[] { input }).structural_slice(root, 2, 120, call_graph_filter_options.none, default);
			var view = call_graph_presentation.build(addresses, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("ldftn is visible before symbolic refinement", view.graph.links.Any(link => link.source == root && Equals(link.target, target) && link.kind == graph_link_kind.address_reference));
			program.check("ldftn never becomes a false execution call", !addresses.links.Any(link => link.source == root && link.target == target));
			var refined_view = call_graph_presentation.build(complete, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("address and real indirect call have distinct relations", refined_view.graph.links.Any(link => link.kind == graph_link_kind.address_reference) && refined_view.graph.links.Any(link => link.kind == graph_link_kind.call && link.label.Contains("calli", StringComparison.Ordinal)));
			var runner = new method_flow_runner(default); var continuation = new method_flow_continuation();
			var straight = program.audit_method(input, "Straight"); straight.Body.Instructions.Clear();
			for (int position = 0; position < 100; position++) straight.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
			straight.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			int executed = 0, slices = 0; method_flow_result flow;
			do { int remaining = 17; slices++; flow = runner.run(straight, new symbolic_state(), (_, _) => executed++, () => --remaining >= 0, continuation: continuation); } while (continuation.is_paused && slices < 20);
			program.check("instruction cursor resumes without replaying completed instructions", slices > 1 && !flow.incomplete && executed == 100);
			program.check_nested_continuation(input, target);
			var analysis = new reachable_call_analysis(new[] { input }, default, workspace) { budget = new refinement_budget(allocation_limit: 0) };
			var first = analysis.select(root, 2, 120, call_graph_filter_options.none);
			program.check("allocation slice preserves continuation rather than finalizing unknown", first.can_continue_refinement);
			using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); analysis.renew(cancelled.Token);
			bool observed = false; try { analysis.select(root, 2, 120, call_graph_filter_options.none); } catch (OperationCanceledException) { observed = true; }
			analysis.renew(default); var resumed = analysis.select(root, 2, 120, call_graph_filter_options.none);
			program.check("cancelled continuation rebinds the next request token", observed && !resumed.can_continue_refinement && program.has(resumed, target));
			var mixed = new call_graph_link(root, target, 2, new uint[] { 0, 5 }, new[] { "call", "callvirt" }, false,
				new[] { new call_site_evidence(0, "call", false, "explicit IL call operand", classification: call_evidence_kind.il_operand), new call_site_evidence(5, "callvirt", true, "unknown receiver candidate") });
			var mixed_view = call_graph_presentation.build(new call_graph_slice(new[] { new call_graph_node(root, 0, true, false), new call_graph_node(target, 1, false, false) }, new[] { mixed }, false), new Dictionary<MethodDef, highlighted_snippet>());
			program.check("direct evidence cannot hide a potential call at another offset", mixed_view.graph.links.Any(link => link.kind == graph_link_kind.structural_call) && mixed_view.graph.links.Any(link => link.kind == graph_link_kind.possible_call));
			program.check_boundaries(input);
			program.check_index_continuation(input);
		}
		static void check_nested_continuation(ModuleDef input, MethodDef target) {
			var factory = program.audit_method(input, "LongFactory"); factory.MethodSig = MethodSig.CreateStatic(input.CorLibTypes.IntPtr); factory.Body.Instructions.Clear();
			for (int position = 0; position < 100; position++) factory.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
			factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, target)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var root = program.audit_method(input, "NestedRoot"); root.Body.Instructions.Clear(); root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, factory)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void))); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			foreach (var method in new[] { root, factory }) method.Body.UpdateInstructionOffsets();
			var workspace = new workspace_analysis(new[] { input }); var analysis = new reachable_call_analysis(new[] { input }, default, workspace) { budget = new refinement_budget(step_limit: 12) };
			var slice = analysis.select(root, 2, 120, call_graph_filter_options.none); int passes = 1;
			while (slice.can_continue_refinement && passes++ < 32) { analysis.renew(default); analysis.budget = new refinement_budget(step_limit: 12); slice = analysis.select(root, 2, 120, call_graph_filter_options.none); }
			program.check("nested helper resumes atomically across many tiny work slices", passes > 2 && passes < 32 && !slice.can_continue_refinement && slice.links.Any(link => link.source == root && link.target == target && link.op_codes.Contains("calli")));
			program.check("nested resume does not retain a false unresolved pointer", !slice.call_sites.Any(site => site.source == root && site.operation == "calli" && site.has_unknown_targets));
			var owner = factory.DeclaringType; var field = new FieldDefUser("pointer", new FieldSig(input.CorLibTypes.IntPtr), dnlib.DotNet.FieldAttributes.Static); owner.Fields.Add(field);
			var initializer = new MethodDefUser(".cctor", MethodSig.CreateStatic(input.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; owner.Methods.Add(initializer);
			for (int position = 0; position < 100; position++) initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
			initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, target)); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I)); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field)); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			root.Body.Instructions.Clear(); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void))); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); initializer.Body.UpdateInstructionOffsets(); root.Body.UpdateInstructionOffsets();
			workspace = new workspace_analysis(new[] { input }); analysis = new reachable_call_analysis(new[] { input }, default, workspace) { budget = new refinement_budget(step_limit: 12) }; slice = analysis.select(root, 2, 120, call_graph_filter_options.none); passes = 1;
			while (slice.can_continue_refinement && passes++ < 64) { analysis.renew(default); analysis.budget = new refinement_budget(step_limit: 12); slice = analysis.select(root, 2, 120, call_graph_filter_options.none); }
			program.check("type initializer resumes with a stable caller frame", passes > 2 && passes < 64 && !slice.can_continue_refinement && slice.links.Any(link => link.source == root && link.target == target && link.op_codes.Contains("calli")));
		}
		static void check_boundaries(ModuleDef input) {
			var root = program.audit_method(input, "BoundaryRoot"); var first = program.audit_method(input, "First"); var second = program.audit_method(input, "Second"); var third = program.audit_method(input, "Third");
			program.audit_call(root, first); program.audit_call(first, second); program.audit_call(second, third);
			var workspace = new workspace_analysis(new[] { input });
			var depth = workspace.browse(root, 120, call_graph_filter_options.none, default).slice;
			program.check("view depth has a distinct diagnostic, not an analysis budget", depth.diagnostics.Any(note => note.method == second && note.kind == graph_diagnostic_kind.view_depth) && !depth.diagnostics.Any(note => note.method == second && note.kind == graph_diagnostic_kind.analysis_budget));
			var limited = workspace.browse(root, 2, call_graph_filter_options.none, default).slice;
			program.check("card limit has an exact cause", limited.diagnostics.Any(note => note.kind == graph_diagnostic_kind.node_limit));
			var filtered = workspace.browse(root, 120, new call_graph_filter_options(only_current_type: true), default).slice;
			program.check("filter exclusion is separate from card limit", filtered.diagnostics.Any(note => note.kind == graph_diagnostic_kind.filtered_target) && !filtered.diagnostics.Any(note => note.kind == graph_diagnostic_kind.node_limit));
			var native = new workspace_analysis(new[] { program.module }).browse(program.fixture("native_boundary"), 120, call_graph_filter_options.none, default).slice;
			program.check("native implementation is an explicit visible boundary", native.diagnostics.Any(note => note.kind == graph_diagnostic_kind.native_boundary) && program.has(native, program.fixture("get_current_process_id")));
			var unknown = new workspace_analysis(new[] { program.module }).browse(program.fixture("unknown_pointer"), 120, call_graph_filter_options.none, default).slice;
			var unknown_view = call_graph_presentation.build(unknown, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("unknown pointer card carries the analysis reason", unknown_view.snippets.Any(pair => pair.Key is unresolved_call_target && pair.Value.text.Contains("context", StringComparison.Ordinal)));
			var unknown_virtual = new workspace_analysis(new[] { program.module }).browse(program.fixture("unknown_virtual"), 120, call_graph_filter_options.none, default).slice;
			program.check("unknown virtual receiver keeps its open-world remainder", unknown_virtual.call_sites.Any(site => site.operation == "callvirt" && site.targets.Count != 0 && site.has_unknown_targets));
			var missing_root = program.audit_method(input, "MissingAddress");
			var missing = new MemberRefUser(input, "Unloaded", MethodSig.CreateStatic(input.CorLibTypes.Void), new TypeRefUser(input, "Unavailable", "API", new AssemblyRefUser("Unavailable", new Version(1, 0))));
			missing_root.Body.Instructions.Clear(); missing_root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, missing)); missing_root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); missing_root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var missing_slice = new workspace_analysis(new[] { input }).browse(missing_root, 120, call_graph_filter_options.none, default).slice;
			var missing_view = call_graph_presentation.build(missing_slice, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("unloaded ldftn target still has a visible address relation", missing_view.graph.links.Any(link => link.source == missing_root && link.target is unresolved_call_target && link.kind == graph_link_kind.address_reference));
			var index = call_graph_index.build(input, default, workspace);
			var incoming = index.select(third, call_graph_direction.callers, 1, 120, workspace: workspace);
			program.check("incoming depth boundary is explicit", incoming.diagnostics.Any(note => note.kind == graph_diagnostic_kind.view_depth));
			var address_owner = program.audit_method(input, "AddressOnlyOwner");
			var initializer = new MethodDefUser(".cctor", MethodSig.CreateStatic(input.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; address_owner.DeclaringType.Methods.Add(initializer); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var address_root = program.audit_method(input, "AddressOnlyRoot"); address_root.Body.Instructions.Clear(); address_root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, address_owner)); address_root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); address_root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			workspace = new workspace_analysis(new[] { input }); index = call_graph_index.build(input, default, workspace); var refined = index.refine(workspace, default);
			program.check("capturing an address does not invent a type-initializer call", !refined.outgoing_value(address_root).Any(link => link.target == initializer));
		}
		static void check_index_continuation(ModuleDef input) {
			var workspace = new workspace_analysis(new[] { input }); var index = call_graph_index.build(input, default, workspace);
			var analysis = new reachable_call_analysis(new[] { input }, default, workspace) { budget = new refinement_budget(step_limit: 20) };
			var partial = index.refine(workspace, default, session: analysis);
			program.check("incoming index saves its unfinished scan", analysis.can_resume_index && partial.select(input.GetTypes().SelectMany(type => type.Methods).First(), call_graph_direction.callers, 2, 120, workspace: workspace).can_continue_refinement);
			int before = analysis.completed_context_count; analysis.renew(default); var complete = index.refine(workspace, default, session: analysis);
			program.check("incoming index continues the saved scan", !analysis.can_resume_index && analysis.completed_context_count > before && index.all_links_value.All(link => complete.outgoing_value(link.source).Any(other => other.target == link.target)));
		}
	}
}
