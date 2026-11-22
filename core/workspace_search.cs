using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	enum workspace_search_kind { methods, strings, callers, field_reads, field_writes, addresses }
	sealed record workspace_search_hit(MethodDef method, uint offset, string description, object? operand = null);
	sealed class workspace_search {
		public MethodDef[] methods { get; }
		readonly workspace_search_hit[] instructions;
		readonly Dictionary<MethodDef, workspace_search_hit[]> calls;
		readonly Dictionary<FieldDef, workspace_search_hit[]> writers;
		public workspace_search(workspace_analysis workspace, CancellationToken token) {
			this.methods = workspace.modules.SelectMany(module => module.GetTypes()).SelectMany(type => type.Methods).ToArray();
			var records = new List<workspace_search_hit>();
			foreach (var method in this.methods) {
				token.ThrowIfCancellationRequested();
				if (!method.HasBody) continue;
				int cursor = 0;
				foreach (var instruction in method.Body.Instructions) {
					if ((cursor++ & 255) == 0) token.ThrowIfCancellationRequested();
					if (instruction.OpCode.Code == Code.Ldstr || instruction.Operand is IMethod or IField) records.Add(new workspace_search_hit(method, instruction.Offset, instruction.OpCode.Name + " " + instruction.Operand, instruction));
				}
			}
			this.instructions = records.ToArray();
			this.calls = records.Where(hit => workspace_search.is_call((Instruction)hit.operand!) && ((Instruction)hit.operand!).Operand is IMethod).Select(hit => { token.ThrowIfCancellationRequested(); return (hit: hit, target: workspace.resolve((IMethod)((Instruction)hit.operand!).Operand)); }).Where(item => item.target is not null).GroupBy(item => item.target!).ToDictionary(group => group.Key, group => group.Select(item => item.hit).ToArray());
			this.writers = records.Where(hit => ((Instruction)hit.operand!).OpCode.Code is Code.Stfld or Code.Stsfld).Select(hit => (hit: hit, field: workspace_search.resolve_field(((Instruction)hit.operand!).Operand as IField))).Where(item => item.field is not null).GroupBy(item => item.field!).ToDictionary(group => group.Key, group => group.Select(item => item.hit).ToArray());
		}
		public static FieldDef? resolve_field(IField? reference) { try { return reference?.ResolveFieldDef(); } catch (Exception) { return null; } }
		static bool is_call(Instruction instruction) => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp;
		public IReadOnlyList<workspace_search_hit> callers(MethodDef method) => this.calls.GetValueOrDefault(method) ?? Array.Empty<workspace_search_hit>();
		public IReadOnlyList<workspace_search_hit> writers_value(FieldDef field) => this.writers.GetValueOrDefault(field) ?? Array.Empty<workspace_search_hit>();
		public workspace_search_hit[] find(workspace_search_kind kind, string query, CancellationToken token) {
			bool matches(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
			if (kind == workspace_search_kind.methods) return this.methods.Where(method => { token.ThrowIfCancellationRequested(); return matches(call_graph_index.signature(method)) || matches($"0x{method.MDToken.Raw:X8}"); }).Select(method => new workspace_search_hit(method, 0, call_graph_index.signature(method))).ToArray();
			return this.instructions.Where(hit => {
				token.ThrowIfCancellationRequested(); var instruction = (Instruction)hit.operand!;
				bool selected = kind switch {
					workspace_search_kind.strings => instruction.OpCode.Code == Code.Ldstr,
					workspace_search_kind.callers => workspace_search.is_call(instruction),
					workspace_search_kind.field_writes => instruction.OpCode.Code is Code.Stfld or Code.Stsfld,
					workspace_search_kind.field_reads => instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda,
					workspace_search_kind.addresses => instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn,
					_ => false,
				};
				return selected && matches(instruction.Operand?.ToString() ?? string.Empty);
			}).ToArray();
		}
		public workspace_search_hit[]? path(MethodDef source, MethodDef target, CancellationToken token) {
			var incoming = new Dictionary<MethodDef, workspace_search_hit>(); var visited = new HashSet<MethodDef> { target }; var pending = new Queue<MethodDef>(); pending.Enqueue(target);
			while (pending.Count != 0) {
				token.ThrowIfCancellationRequested(); var method = pending.Dequeue(); if (method == source) break;
				foreach (var hit in this.callers(method)) if (visited.Add(hit.method)) { incoming[hit.method] = hit with { operand = method }; pending.Enqueue(hit.method); }
			}
			if (!visited.Contains(source)) return null;
			var path = new List<workspace_search_hit>(); var cursor = source;
			while (cursor != target) { var hit = incoming[cursor]; path.Add(hit); cursor = (MethodDef)hit.operand!; }
			return path.ToArray();
		}
	}
}
