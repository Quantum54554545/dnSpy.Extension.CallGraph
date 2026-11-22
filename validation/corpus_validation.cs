using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

namespace CallGraph.Validation {
	static partial class program {
		static bool is_call(Instruction instruction) => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp or Code.Calli;
		static void check_generated_graphs() {
			for (int seed = 0; seed < 16; seed++) {
				var signatures = new List<string>(); bool complete = true;
				foreach (bool rename in new[] { false, true }) {
					using var generated = new ModuleDefUser(rename ? "Σ" + seed : "Graph" + seed);
					new AssemblyDefUser(generated.Name, new Version(1, seed)).Modules.Add(generated);
					var owner = new TypeDefUser(rename ? "пространство" : "Generated", rename ? "τύπος" : "Calls", generated.CorLibTypes.Object.TypeDefOrRef);
					generated.Types.Add(owner);
					var methods = Enumerable.Range(0, 18).Select(index => new MethodDefUser(rename ? "λ_" + (100 - index) : "Method" + index,
						MethodSig.CreateStatic(generated.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
						dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.Public)).ToArray();
					foreach (var method in rename ? methods.Reverse() : methods) owner.Methods.Add(method);
					var random = new Random(seed);
					for (int index = 0; index < methods.Length; index++) {
						if (index > 0 && index % 7 == 0) { methods[index].ImplAttributes = dnlib.DotNet.MethodImplAttributes.Native | dnlib.DotNet.MethodImplAttributes.Unmanaged; continue; }
						var body = new CilBody(); methods[index].Body = body;
						var last = Instruction.Create(OpCodes.Ret);
						if (index % 3 == 0) { body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, last)); }
						if (index % 4 == 0) { body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); body.Instructions.Add(Instruction.Create(OpCodes.Throw)); }
						foreach (int target in new[] { (index + 1) % methods.Length, random.Next(methods.Length), random.Next(methods.Length) })
							body.Instructions.Add(Instruction.Create(OpCodes.Call, methods[target]));
						body.Instructions.Add(last); body.UpdateInstructionOffsets();
					}
					var reachable = new HashSet<MethodDef>(); var expected = new HashSet<(MethodDef source, MethodDef target, uint offset)>();
					var pending = new Queue<MethodDef>(); pending.Enqueue(methods[0]);
					while (pending.Count != 0) {
						var current = pending.Dequeue(); if (!reachable.Add(current) || !current.HasBody) continue;
						foreach (var instruction in current.Body.Instructions.Where(program.is_call)) {
							var target = (MethodDef)instruction.Operand; expected.Add((source: current, target: target, offset: instruction.Offset)); pending.Enqueue(target);
						}
					}
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
					var slice = new workspace_analysis(new[] { generated }).analyze(methods[0], int.MaxValue, 100, call_graph_filter_options.none, null, timeout.Token).slice;
					var actual = slice.links.SelectMany(link => link.il_offsets.Select(offset => (source: link.source, target: link.target, offset: offset))).ToHashSet();
					complete &= expected.SetEquals(actual) && reachable.SetEquals(slice.nodes.Select(node => node.method));
					var indices = methods.Select((method, index) => (method: method, index: index)).ToDictionary(pair => (MethodDef)pair.method, pair => pair.index);
					signatures.Add(string.Join(";", actual.Select(edge => indices[edge.source] + ":" + indices[edge.target] + ":" + edge.offset).OrderBy(value => value, StringComparer.Ordinal)));
				}
				program.check("generated graph preserves every IL edge, seed " + seed, complete);
				program.check("renaming and metadata order preserve topology, seed " + seed, signatures[0] == signatures[1]);
			}
		}
		
	}
}
