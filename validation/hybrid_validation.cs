using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;

namespace CallGraph.Validation {
	static partial class program {
		static void check_hybrid() {
			var workspace = new workspace_analysis(new[] { program.module });
			var options = new call_graph_filter_options(only_current_assembly: true);
			foreach (var item in new[] { (name: "pointer", target: program.fixture("leaf")), (name: "field_pointer", target: program.fixture("leaf")),
				(name: "mutated_pointer", target: program.fixture("leaf")), (name: "delegate", target: program.fixture("leaf")),
				(name: "reflection", target: program.fixture("leaf")), (name: "virtual", target: program.method("derived", "run")),
				(name: "async", target: program.module.GetTypes().SelectMany(type => type.Methods).First(method => method.Name == "MoveNext" && method.DeclaringType.Name.ToString().Contains("<" + nameof(fixtures.async) + ">"))) }) {
				var root = program.fixture(item.name); var progress = new progress_sink();
				var result = workspace.browse(root, 120, options, default, progress).slice;
				program.check("hybrid restores " + item.name + " runtime target", result.links.Any(link => link.source == root && link.target == item.target));
				program.check("hybrid retains structural preview edges for " + item.name, progress.values.Count == 1 && progress.values[0].links.All(link => result.links.Any(other => other.source == link.source && other.target == link.target && link.il_offsets.All(other.il_offsets.Contains))));
				if (item.name == "pointer") {
					var view = call_graph_presentation.build(result, new Dictionary<MethodDef, highlighted_snippet>());
					program.check("fully resolved pointer does not keep a false unknown card", !view.graph.links.Any(link => link.source == root && link.target is unresolved_call_target));
				}
			}
			var constrained = workspace.browse(program.fixture("constrained"), 120, options, default).slice;
			program.check("hybrid restores constrained generic dispatch", program.has(constrained, program.method("value", "run")));
			var branch = workspace.browse(program.fixture("known_branch"), 120, options, default).slice;
			program.check("hybrid target inference never removes the other IL branch", program.has(branch, program.fixture("leaf")) && program.has(branch, program.fixture("other_leaf")));
			foreach (string name in new[] { "after_loader_failure", "after_failed_initializer", "crypto_guard" }) {
				var root = program.fixture(name); var baseline = workspace.structural_slice(root, 2, 120, options, default);
				var result = workspace.browse(root, 120, options, default).slice;
				program.check("hybrid preserves IL after inferred failure: " + name, baseline.links.All(link => result.links.Any(other => other.source == link.source && other.target == link.target && link.il_offsets.All(other.il_offsets.Contains))));
			}
			var unknown_root = program.fixture("unknown_pointer"); var call = unknown_root.Body.Instructions.First(instruction => instruction.OpCode.Code == Code.Calli);
			var possible = new call_graph_link(unknown_root, program.fixture("leaf"), 1, new[] { call.Offset }, new[] { "calli" }, true);
			var partial = new call_graph_slice(new[] { new call_graph_node(unknown_root, 0, true, false), new call_graph_node(program.fixture("leaf"), 1, false, false) }, new[] { possible }, true, 1,
				new[] { new graph_diagnostic(unknown_root, call.Offset, graph_diagnostic_kind.unresolved_pointer, "remaining pointer targets are unknown") });
			var merged = workspace.structural_slice(unknown_root, 2, 120, options, default, partial, workspace.structural_slice(unknown_root, 2, 120, options, default));
			var presentation = call_graph_presentation.build(merged, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("partial target set retains both the known edge and uncertainty", presentation.graph.links.Any(link => link.source == unknown_root && Equals(link.target, program.fixture("leaf"))) && presentation.graph.links.Any(link => link.source == unknown_root && link.target is unresolved_call_target));
			int count = workspace.analysis_count; workspace.browse(program.fixture("pointer"), 120, options, default);
			program.check("hybrid result is reused without reinterpreting the root", count == workspace.analysis_count);
			var limited_root = program.fixture("pointer_and_sibling");
			var limited_baseline = workspace.structural_slice(limited_root, 2, 2, options, default);
			var limited_result = workspace.browse(limited_root, 2, options, default).slice;
			program.check("dynamic additions cannot displace structural nodes at a full node budget", limited_baseline.nodes.All(node => limited_result.nodes.Any(other => other.method == node.method)) && program.has(limited_result, program.fixture("other_leaf")));
			program.check("resolved target outside node budget has a continuation hint", limited_result.call_sites.Any(site => site.source == limited_root && site.operation == "calli" && site.targets.Contains(program.fixture("leaf"))) && limited_result.nodes.Single(node => node.method == limited_root).expansion_hint.Length != 0);
			var partial_flow = workspace.browse(program.fixture("partial_pointer"), 120, options, default).slice;
			var partial_view = call_graph_presentation.build(partial_flow, new Dictionary<MethodDef, highlighted_snippet>());
			program.check("real partial pointer flow keeps its known target and residual unknown", partial_view.graph.links.Any(link => link.source == program.fixture("partial_pointer") && Equals(link.target, program.fixture("leaf"))) && partial_view.graph.links.Any(link => link.source == program.fixture("partial_pointer") && link.target is unresolved_call_target));
			var structural_index = call_graph_index.build(program.module, default, workspace); var refined_index = workspace.refine_index(structural_index, default);
			program.check("incoming refinement retains every structural indexed edge", structural_index.all_links_value.All(link => refined_index.outgoing_value(link.source).Any(other => other.target == link.target && link.il_offsets.All(other.il_offsets.Contains))));
			var incoming = refined_index.select(program.fixture("leaf"), call_graph_direction.callers, 2, 500, options, default, workspace);
			program.check("caller view restores resolved indirect callers", incoming.links.Any(link => link.source == program.fixture("pointer") && link.target == program.fixture("leaf") && link.op_codes.Contains("calli")));
			var forward = workspace.browse(program.fixture("pointer"), 120, options, default).slice;
			var both = refined_index.select(program.fixture("pointer"), call_graph_direction.both, 2, 500, options, default, workspace, false, forward);
			program.check("both directions retain the hybrid forward target", both.links.Any(link => link.source == program.fixture("pointer") && link.target == program.fixture("leaf") && link.op_codes.Contains("calli")));
			program.check_pointer_mutations();
		}
		static void check_pointer_mutations() {
			for (int seed = 0; seed < 12; seed++) {
				bool complete = true;
				foreach (bool renamed in new[] { false, true }) {
					using var input = program.audit_module(renamed ? "Мутация" + seed : "PointerMutation" + seed);
					var owner = new TypeDefUser(renamed ? "пространство" : "Mutation", renamed ? "τύπος" : "Calls", input.CorLibTypes.Object.TypeDefOrRef); input.Types.Add(owner);
					MethodDef make(string name, MethodSig signature) {
						var method = new MethodDefUser(name, signature, dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.Public) { Body = new CilBody() };
						owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
					}
					var root = make(renamed ? "λ_корень" : "Root", MethodSig.CreateStatic(input.CorLibTypes.Void));
					var factory = make(renamed ? "α_выбор" : "factory", MethodSig.CreateStatic(input.CorLibTypes.IntPtr, input.CorLibTypes.Int32));
					var leaves = Enumerable.Range(0, 4).Select(index => make(renamed ? "ψ_" + (100 - index) : "leaf" + index, MethodSig.CreateStatic(input.CorLibTypes.Void))).ToArray();
					var sibling = make(renamed ? "Ω" : "Sibling", MethodSig.CreateStatic(input.CorLibTypes.Void));
					var pointer = new FieldDefUser(renamed ? "φ" : "pointer", new FieldSig(input.CorLibTypes.IntPtr), dnlib.DotNet.FieldAttributes.Static | dnlib.DotNet.FieldAttributes.Public); owner.Fields.Add(pointer);
					var initializer = make(".cctor", MethodSig.CreateStatic(input.CorLibTypes.Void)); initializer.Attributes |= dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName;
					initializer.Body.Instructions.Clear(); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 41)); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Call, factory));
					initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, pointer)); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
					factory.Body.Instructions.Clear();
					foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4, seed), Instruction.Create(OpCodes.Xor),
						Instruction.Create(OpCodes.Ldc_I4_3), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Ldc_I4_5), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ldc_I4_3), Instruction.Create(OpCodes.And) }) factory.Body.Instructions.Add(instruction);
					var labels = leaves.Select(leaf => Instruction.Create(OpCodes.Ldftn, leaf)).ToArray(); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Switch, labels));
					factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
					foreach (var label in labels) { factory.Body.Instructions.Add(label); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
					root.Body.Instructions.Clear(); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, pointer)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(input.CorLibTypes.Void)));
					root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, sibling)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
					if (renamed) {
						var methods = owner.Methods.Reverse().ToArray(); owner.Methods.Clear(); foreach (var method in methods) owner.Methods.Add(method);
						foreach (var method in owner.Methods) for (int position = method.Body.Instructions.Count - 1; position >= 0; position--) method.Body.Instructions.Insert(position, Instruction.Create(OpCodes.Nop));
					}
					foreach (var method in owner.Methods) method.Body.UpdateInstructionOffsets();
					var workspace = new workspace_analysis(new[] { input }); var slice = workspace.browse(root, 120, call_graph_filter_options.none, default).slice;
					var expected = leaves[(((41 ^ seed) * 3 + 5) & 3)];
					complete &= slice.links.Any(link => link.source == root && link.target == expected && link.op_codes.Contains("calli")) && slice.links.Any(link => link.source == root && link.target == sibling);
				}
				program.check("field/cctor/arithmetic/switch pointer survives renaming, reordering and NOPs, seed " + seed, complete);
			}
		}
		
	}
}
