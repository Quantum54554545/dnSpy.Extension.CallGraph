using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	static class IlStackValidation {
		public static string? Check(MethodDef method, CancellationToken cancellation_token) {
			var instructions = method.Body.Instructions;
			if (instructions.Count == 0) return null;
			var positions = instructions.Select((instruction, index) => (instruction, index)).ToDictionary(pair => pair.instruction, pair => pair.index);
			var depths = new Dictionary<int, int>();
			var pending = new Queue<(int Position, int Depth)>(); pending.Enqueue((0, 0));
			while (pending.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var (position, depth) = pending.Dequeue();
				if (position < 0 || position >= instructions.Count) return "invalid IL fall-through past method body";
				if (depths.TryGetValue(position, out int previous)) { if (previous != depth) return "invalid IL stack depths at control-flow join"; continue; }
				depths.Add(position, depth);
				var instruction = instructions[position]; var code = instruction.OpCode.Code;
				if (code == Code.Ret) {
					if (depth != (!CallSignature.ReturnsValue(method.MethodSig) ? 0 : 1)) return "invalid IL stack at return";
					continue;
				}
				if (code == Code.Jmp) {
					if (depth != 0 || instruction.Operand is not IMethod destination || !new SigComparer().Equals(method.MethodSig, destination.MethodSig)) return "invalid IL jmp signature or stack";
					continue;
				}
				instruction.CalculateStackUsage(out int pushes, out int pops);
				if (pops >= 0 && depth < pops) return "invalid IL evaluation stack underflow";
				depth = pops < 0 ? 0 : depth - pops + pushes;
				if (depth > 4096) return "analysis stack size limit reached";
				if (instruction.OpCode.FlowControl is FlowControl.Throw or FlowControl.Return) continue;
				if (code == Code.Switch) {
					if (instruction.Operand is not IList<Instruction> targets) return "invalid IL switch operand";
					foreach (var target in targets) { if (!positions.TryGetValue(target, out int next)) return "invalid IL branch target"; pending.Enqueue((next, depth)); }
				}
				else if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) {
					if (instruction.Operand is not Instruction target || !positions.TryGetValue(target, out int next)) return "invalid IL branch target";
					pending.Enqueue((next, code is Code.Leave or Code.Leave_S ? 0 : depth));
					if (instruction.OpCode.FlowControl == FlowControl.Branch) continue;
				}
				pending.Enqueue((position + 1, depth));
			}
			return null;
		}
	}
}
