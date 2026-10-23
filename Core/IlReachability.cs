using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	static class IlReachability {
		public static HashSet<uint>? Find(MethodDef method, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			var instructions = method.Body.Instructions;
			if (instructions.Count == 0 || !instructions.Any(item => item.OpCode.FlowControl == FlowControl.Cond_Branch || item.OpCode.FlowControl == FlowControl.Branch) && !instructions.Take(instructions.Count - 1).Any(item => item.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw || item.OpCode.Code == Code.Jmp)) return null;
			var positions = instructions.Select((instruction, index) => (instruction, index)).ToDictionary(pair => pair.instruction, pair => pair.index);
			var leaders = new HashSet<int> { 0 };
			for (int i = 0; i < instructions.Count; i++) {
				if ((i & 1023) == 0) cancellation_token.ThrowIfCancellationRequested();
				if (instructions[i].Operand is Instruction target) leaders.Add(positions[target]);
				if (instructions[i].Operand is IList<Instruction> targets) foreach (var target_item in targets) leaders.Add(positions[target_item]);
				if (instructions[i].OpCode.FlowControl == FlowControl.Branch || instructions[i].OpCode.FlowControl == FlowControl.Cond_Branch || instructions[i].OpCode.FlowControl == FlowControl.Return || instructions[i].OpCode.FlowControl == FlowControl.Throw) leaders.Add(i + 1);
			}
			foreach (var handler in method.Body.ExceptionHandlers) {
				if (handler.HandlerStart is not null) leaders.Add(positions[handler.HandlerStart]);
				if (handler.FilterStart is not null) leaders.Add(positions[handler.FilterStart]);
			}
			var starts = leaders.OrderBy(index => index).ToArray();
			int constant_block_start = 0;
			var reached = new HashSet<int>();
			var queue = new Queue<int>(); queue.Enqueue(0);
			foreach (var handler in method.Body.ExceptionHandlers) {
				if (handler.HandlerStart is not null) queue.Enqueue(positions[handler.HandlerStart]);
				if (handler.FilterStart is not null) queue.Enqueue(positions[handler.FilterStart]);
			}
			while (queue.Count != 0) {
				if ((reached.Count & 1023) == 0) cancellation_token.ThrowIfCancellationRequested();
				int index = queue.Dequeue();
				if (index < 0 || index >= instructions.Count || !reached.Add(index)) continue;
				var instruction = instructions[index];
				int leader_position = Array.BinarySearch(starts, index);
				constant_block_start = starts[leader_position >= 0 ? leader_position : ~leader_position - 1];
				var code = instruction.OpCode.Code;
				if (instruction.OpCode.FlowControl == FlowControl.Return || instruction.OpCode.FlowControl == FlowControl.Throw || code == Code.Jmp || code == Code.Endfinally || code == Code.Endfilter) continue;
				if (code == Code.Switch && instruction.Operand is IList<Instruction> targets) {
					int previous = index - 1, budget = 48;
					if (Read(ref previous, ref budget, out int selector)) {
						queue.Enqueue(selector >= 0 && selector < targets.Count ? positions[targets[selector]] : index + 1);
					}
					else { foreach (var target in targets) queue.Enqueue(positions[target]); queue.Enqueue(index + 1); }
					continue;
				}
				if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch && instruction.Operand is Instruction conditional) {
					bool? taken = Condition(index, code);
					if (taken != false) queue.Enqueue(positions[conditional]);
					if (taken != true) queue.Enqueue(index + 1);
				}
				else if (instruction.OpCode.FlowControl == FlowControl.Branch && instruction.Operand is Instruction branch) queue.Enqueue(positions[branch]);
				else queue.Enqueue(index + 1);
			}
			return reached.Count == instructions.Count ? null : new HashSet<uint>(reached.Select(index => instructions[index].Offset));

			bool? Condition(int index, Code code) {
				int previous = index - 1, budget = 48;
				if (!Read(ref previous, ref budget, out int right)) return null;
				if (code == Code.Brtrue || code == Code.Brtrue_S) return right != 0;
				if (code == Code.Brfalse || code == Code.Brfalse_S) return right == 0;
				if (!Read(ref previous, ref budget, out int left)) return null;
				return code switch {
					Code.Beq or Code.Beq_S => left == right, Code.Bne_Un or Code.Bne_Un_S => left != right,
					Code.Bge or Code.Bge_S => left >= right, Code.Bgt or Code.Bgt_S => left > right,
					Code.Ble or Code.Ble_S => left <= right, Code.Blt or Code.Blt_S => left < right,
					Code.Bge_Un or Code.Bge_Un_S => (uint)left >= (uint)right, Code.Bgt_Un or Code.Bgt_Un_S => (uint)left > (uint)right,
					Code.Ble_Un or Code.Ble_Un_S => (uint)left <= (uint)right, Code.Blt_Un or Code.Blt_Un_S => (uint)left < (uint)right,
					_ => null,
				};
			}
			bool Read(ref int previous, ref int budget, out int value) {
				value = 0;
				while (previous >= 0 && instructions[previous].OpCode.Code == Code.Nop) previous--;
				if (previous < constant_block_start || --budget < 0) return false;
				var item = instructions[previous--];
				if (item.IsLdcI4()) { value = item.GetLdcI4Value(); return true; }
				var op = item.OpCode.Code;
				if (op == Code.Conv_I4 || op == Code.Conv_U4) return Read(ref previous, ref budget, out value);
				if (op == Code.Neg || op == Code.Not) {
					if (!Read(ref previous, ref budget, out int operand)) return false;
					value = op == Code.Neg ? unchecked(-operand) : ~operand; return true;
				}
				if (op != Code.Add && op != Code.Sub && op != Code.Mul && op != Code.Xor && op != Code.And && op != Code.Or && op != Code.Shl && op != Code.Shr && op != Code.Shr_Un && op != Code.Ceq) return false;
				if (!Read(ref previous, ref budget, out int right) || !Read(ref previous, ref budget, out int left)) return false;
				value = op switch {
					Code.Add => unchecked(left + right), Code.Sub => unchecked(left - right), Code.Mul => unchecked(left * right),
					Code.Xor => left ^ right, Code.And => left & right, Code.Or => left | right,
					Code.Shl => left << right, Code.Shr => left >> right, Code.Shr_Un => unchecked((int)((uint)left >> right)), Code.Ceq => left == right ? 1 : 0,
					_ => 0,
				};
				return true;
			}
		}
	}
}
