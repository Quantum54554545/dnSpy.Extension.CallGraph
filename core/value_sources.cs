using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record value_origin(MethodDef method, int position);
	sealed record value_dependency(value_origin source, string reason, string label = "");
	sealed class method_value_sources {
		sealed class state {
			public List<HashSet<int>> stack = new();
			public Dictionary<int, HashSet<int>> locals = new();
			public Dictionary<int, HashSet<int>> arguments = new();
			public state copy() => new() { stack = this.stack.Select(value => new HashSet<int>(value)).ToList(), locals = this.locals.ToDictionary(pair => pair.Key, pair => new HashSet<int>(pair.Value)), arguments = this.arguments.ToDictionary(pair => pair.Key, pair => new HashSet<int>(pair.Value)) };
			public bool merge(state other) {
				bool changed = false;
				if (this.stack.Count != other.stack.Count) { int count = Math.Max(this.stack.Count, other.stack.Count); while (this.stack.Count < count) { this.stack.Add(new HashSet<int> { -1 }); changed = true; } }
				for (int index = 0; index < this.stack.Count; index++) { int count = this.stack[index].Count; this.stack[index].UnionWith(index < other.stack.Count ? other.stack[index] : new HashSet<int> { -1 }); changed |= this.stack[index].Count != count; }
				foreach (var key in this.locals.Keys.Concat(other.locals.Keys).Distinct().ToArray()) { if (!this.locals.TryGetValue(key, out var value)) this.locals[key] = value = new HashSet<int> { -1 }; int count = value.Count; value.UnionWith(other.locals.GetValueOrDefault(key) ?? new HashSet<int> { -1 }); changed |= value.Count != count; }
				foreach (var key in this.arguments.Keys.Concat(other.arguments.Keys).Distinct().ToArray()) { if (!this.arguments.TryGetValue(key, out var value)) this.arguments[key] = value = new HashSet<int> { -1 }; int count = value.Count; value.UnionWith(other.arguments.GetValueOrDefault(key) ?? new HashSet<int> { -1 }); changed |= value.Count != count; }
				return changed;
			}
		}
		public MethodDef method { get; }
		public Dictionary<int, HashSet<int>> dependencies { get; } = new();
		public Dictionary<int, HashSet<int>[]> inputs { get; } = new();
		public bool incomplete { get; private set; }
		public method_value_sources(MethodDef method, CancellationToken token) {
			this.method = method; if (!method.HasBody || method.Body.Instructions.Count == 0) return;
			var instructions = method.Body.Instructions;
			var positions = instructions.Select((instruction, index) => (instruction: instruction, index: index)).ToDictionary(item => item.instruction, item => item.index);
			var states = new Dictionary<int, global::dnSpy.CallGraph.Core.method_value_sources.state>(); var queued = new HashSet<int>(); var pending = new Queue<int>();
			void enqueue(int position, global::dnSpy.CallGraph.Core.method_value_sources.state state) {
				if (position < 0 || position >= instructions.Count) { this.incomplete = true; return; }
				if (states.TryGetValue(position, out var existing)) { if (existing.stack.Count != state.stack.Count) this.incomplete = true; if (!existing.merge(state)) return; } else states[position] = state.copy();
				if (queued.Add(position)) pending.Enqueue(position);
			}
			enqueue(0, new global::dnSpy.CallGraph.Core.method_value_sources.state());
			foreach (var handler in method.Body.ExceptionHandlers) {
				if (handler.HandlerStart is not null && positions.TryGetValue(handler.HandlerStart, out int start)) { var state = new global::dnSpy.CallGraph.Core.method_value_sources.state(); if (handler.IsCatch || handler.IsFilter) state.stack.Add(new HashSet<int> { -2 }); enqueue(start, state); }
				if (handler.FilterStart is not null && positions.TryGetValue(handler.FilterStart, out int filter)) { var state = new global::dnSpy.CallGraph.Core.method_value_sources.state(); state.stack.Add(new HashSet<int> { -2 }); enqueue(filter, state); }
			}
			int steps = 0;
			while (pending.Count != 0) {
				token.ThrowIfCancellationRequested(); if (++steps > 250000) { this.incomplete = true; break; }
				int position = pending.Dequeue(); queued.Remove(position); var state = states[position].copy(); var instruction = instructions[position]; var code = instruction.OpCode.Code;
				if (code is Code.Ldobj or Code.Stobj or Code.Cpobj or Code.Cpblk or Code.Initblk or Code.Initobj || instruction.OpCode.Name.StartsWith("ldind", StringComparison.Ordinal) || instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal)) this.incomplete = true;
				instruction.CalculateStackUsage(out int pushes, out int pops);
				if (code == Code.Ret) pops = call_signature.returns_value(method.MethodSig) ? 1 : 0;
				if (pops < 0) pops = state.stack.Count;
				var consumed = new List<HashSet<int>>();
				for (int index = 0; index < pops; index++) { if (state.stack.Count == 0) { consumed.Add(new HashSet<int> { -1 }); this.incomplete = true; } else { consumed.Add(state.stack[^1]); state.stack.RemoveAt(state.stack.Count - 1); } }
				consumed.Reverse();
				if (!this.dependencies.TryGetValue(position, out var dependencies)) this.dependencies[position] = dependencies = new HashSet<int>();
				foreach (var value in consumed) dependencies.UnionWith(value);
				if (!this.inputs.TryGetValue(position, out var inputs)) this.inputs[position] = inputs = consumed.Select(value => new HashSet<int>(value)).ToArray();
				else if (inputs.Length == consumed.Count) for (int index = 0; index < inputs.Length; index++) inputs[index].UnionWith(consumed[index]);
				else this.incomplete = true;
				int local = method_value_sources.local_index(instruction);
				if (instruction.IsStloc()) state.locals[local] = new HashSet<int> { position };
				if (instruction.IsLdloc() || code is Code.Ldloca or Code.Ldloca_S) dependencies.UnionWith(state.locals.GetValueOrDefault(local) ?? new HashSet<int> { -1 });
				int argument = method_value_sources.argument_index(instruction);
				if (code is Code.Starg or Code.Starg_S) state.arguments[argument] = new HashSet<int> { position };
				if (instruction.IsLdarg() || code is Code.Ldarga or Code.Ldarga_S) if (state.arguments.TryGetValue(argument, out var assigned)) dependencies.UnionWith(assigned);
				for (int index = 0; index < pushes; index++) state.stack.Add(new HashSet<int> { position });
				if (state.stack.Count > 4096) { this.incomplete = true; continue; }
				if (instruction.OpCode.FlowControl is FlowControl.Throw or FlowControl.Return || code == Code.Jmp) continue;
				if (code is Code.Leave or Code.Leave_S) state.stack.Clear();
				if (instruction.Operand is Instruction target && instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) { if (positions.TryGetValue(target, out int next)) enqueue(next, state); else this.incomplete = true; if (instruction.OpCode.FlowControl == FlowControl.Branch) continue; }
				if (code == Code.Switch && instruction.Operand is IList<Instruction> targets) foreach (var item in targets) { if (positions.TryGetValue(item, out int next)) enqueue(next, state); else this.incomplete = true; }
				enqueue(position + 1, state);
			}
		}
		static int local_index(Instruction instruction) => instruction.OpCode.Code switch { Code.Ldloc_0 or Code.Stloc_0 => 0, Code.Ldloc_1 or Code.Stloc_1 => 1, Code.Ldloc_2 or Code.Stloc_2 => 2, Code.Ldloc_3 or Code.Stloc_3 => 3, _ => (instruction.Operand as Local)?.Index ?? -1 };
		public static int argument_index(Instruction instruction) => instruction.OpCode.Code switch { Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => (instruction.Operand as Parameter)?.Index ?? -1 };
	}
	sealed class value_sources {
		readonly workspace_analysis workspace;
		readonly workspace_search index;
		readonly Dictionary<MethodDef, method_value_sources> methods = new();
		public value_sources(workspace_analysis workspace, workspace_search index) { this.workspace = workspace; this.index = index; }
		public method_value_sources method(MethodDef method, CancellationToken token) { if (!this.methods.TryGetValue(method, out var result)) this.methods[method] = result = new method_value_sources(method, token); return result; }
		public value_dependency[] parents(value_origin origin, CancellationToken token) {
			var method = origin.method;
			if (origin.position < 0 || !method.HasBody || origin.position >= method.Body.Instructions.Count) return Array.Empty<value_dependency>();
			var instruction = method.Body.Instructions[origin.position]; var model = this.method(method, token); var result = new List<value_dependency>();
			if (!model.inputs.ContainsKey(origin.position)) result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("No modeled entry state for this IL instruction", "Для этой IL-инструкции не получено входное состояние")));
			if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj && model.inputs.TryGetValue(origin.position, out var call_inputs)) {
				bool has_this = instruction.OpCode.Code != Code.Newobj && (instruction.Operand is IMethod called ? called.MethodSig?.HasThis == true : (instruction.Operand as MethodSig)?.HasThis == true);
				for (int index = 0; index < call_inputs.Length; index++) {
					bool pointer = instruction.OpCode.Code == Code.Calli && index == call_inputs.Length - 1; bool receiver = has_this && index == 0 && !pointer;
					string label = pointer ? "fnptr" : receiver ? "this" : "arg " + (index - (has_this ? 1 : 0));
					string reason = pointer ? ui_text.pick("Function address consumed by calli", "Адрес функции, потребляемый calli") : receiver ? ui_text.pick("Receiver consumed by the call", "Получатель this для вызова") : ui_text.pick("Call argument ", "Аргумент вызова ") + (index - (has_this ? 1 : 0));
					foreach (int parent in call_inputs[index]) result.Add(new value_dependency(new value_origin(method, parent), reason, label));
				}
			}
			else foreach (int parent in model.dependencies.GetValueOrDefault(origin.position) ?? new HashSet<int>()) result.Add(new value_dependency(new value_origin(method, parent), ui_text.pick("IL value dependency; path not proven", "Зависимость значения по IL; путь не доказан")));
			if (instruction.OpCode.Code == Code.Calli && instruction.Operand is MethodSig indirect_signature && call_signature.returns_value(indirect_signature)) result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Indirect return value; runtime target bodies are not linked by this data-flow view", "Возвращаемое значение косвенного вызова; тела runtime-целей в этом режиме не связаны")));
			if (instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda) {
				var field = workspace_search.resolve_field(instruction.Operand as IField);
				if (field is null) result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Field metadata unavailable", "Метаданные поля недоступны")));
				else {
					foreach (var hit in this.index.writers_value(field)) result.Add(new value_dependency(new value_origin(hit.method, hit.method.Body.Instructions.IndexOf((Instruction)hit.operand!)), ui_text.pick("Possible field write; order and instance are not proven", "Возможная запись поля; порядок и экземпляр не доказаны")));
					result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Default value or external/indirect write may also apply", "Возможно начальное значение или внешняя/косвенная запись")));
				}
			}
			if ((instruction.OpCode.Code is Code.Call or Code.Callvirt) && instruction.Operand is IMethod reference && call_signature.returns_value(reference.MethodSig)) {
				var target = this.workspace.resolve(reference);
				if (target?.HasBody == true) {
					for (int position = 0; position < target.Body.Instructions.Count; position++) if (target.Body.Instructions[position].OpCode.Code == Code.Ret) result.Add(new value_dependency(new value_origin(target, position), ui_text.pick("Possible returned value of declared target", "Возможное возвращаемое значение объявленной цели")));
				}
				if (target?.HasBody != true || instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Runtime/native return is not fully known", "Возврат runtime/native-цели известен не полностью")));
			}
			if (instruction.IsLdarg() || instruction.OpCode.Code is Code.Ldarga or Code.Ldarga_S) {
				int argument = method_value_sources.argument_index(instruction);
				foreach (var hit in this.index.callers(method)) {
					int call_position = hit.method.Body.Instructions.IndexOf((Instruction)hit.operand!); var caller = this.method(hit.method, token);
					int input_index = argument - (((Instruction)hit.operand!).OpCode.Code == Code.Newobj ? 1 : 0);
					if (caller.inputs.TryGetValue(call_position, out var inputs) && input_index >= 0 && input_index < inputs.Length) foreach (int parent in inputs[input_index]) result.Add(new value_dependency(new value_origin(hit.method, parent), ui_text.pick("Argument at a direct IL call site; context not proven", "Аргумент прямого места вызова; контекст не доказан")));
				}
				result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Other callers and runtime arguments remain possible", "Возможны другие вызывающие и runtime-аргументы")));
			}
			if (model.incomplete) result.Add(new value_dependency(new value_origin(method, -1), ui_text.pick("Value-flow model incomplete; do not infer absence", "Модель потока значений неполная; отсутствие зависимости не доказано")));
			return result.Distinct().ToArray();
		}
	}
}
