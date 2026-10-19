using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Text;

namespace dnSpy.CallGraph.Core {
	enum ControlFlowEdgeKind {
		FallThrough,
		Conditional,
		Unconditional,
		Switch,
		Exception,
	}

	sealed class ControlFlowBlock {
		public MethodDef Method { get; }
		public int Index { get; }
		public IReadOnlyList<Instruction> Instructions { get; }
		public uint StartOffset => Instructions[0].Offset;
		public uint EndOffset => Instructions[Instructions.Count - 1].Offset;
		public int Layer { get; internal set; }
		public bool IsEntry => Index == 0;
		public string Title => "IL_" + StartOffset.ToString("X4", CultureInfo.InvariantCulture);
		public string Description => DescribeTerminator(Instructions[Instructions.Count - 1]);
		public HighlightedSnippet Snippet { get; }

		public ControlFlowBlock(MethodDef method, int index, IReadOnlyList<Instruction> instructions) {
			Method = method;
			Index = index;
			Instructions = instructions;
			Snippet = BuildSnippet(instructions);
		}

		static string DescribeTerminator(Instruction instruction) {
			if (instruction.OpCode.Code == Code.Switch)
				return UiText.Pick("switch", "переключатель");
			return instruction.OpCode.FlowControl switch {
				FlowControl.Cond_Branch => UiText.Pick("conditional branch", "условный переход"),
				FlowControl.Branch => UiText.Pick("unconditional branch", "безусловный переход"),
				FlowControl.Return => UiText.Pick("return", "возврат"),
				FlowControl.Throw => UiText.Pick("throw", "исключение"),
				_ => UiText.Pick("fall through", "следующий блок"),
			};
		}

		static HighlightedSnippet BuildSnippet(IReadOnlyList<Instruction> instructions) {
			var builder = new SnippetBuilder();
			for (int i = 0; i < instructions.Count; i++) {
				var instruction = instructions[i];
				builder.Append("IL_" + instruction.Offset.ToString("X4", CultureInfo.InvariantCulture) + ":", TextColor.Label);
				builder.Append("  ", TextColor.Text);
				builder.Append(instruction.OpCode.Name, TextColor.OpCode);
				string operand = FormatOperand(instruction.Operand);
				if (operand.Length != 0) {
					builder.Append(" ", TextColor.Text);
					builder.Append(operand, OperandColor(instruction.Operand), instruction.Operand is IMethod ? instruction.Operand : null);
				}
				if (i + 1 < instructions.Count)
					builder.AppendLine();
			}
			return builder.Create();
		}

		static TextColor OperandColor(object? operand) => operand switch {
			string => TextColor.String,
			Instruction => TextColor.Label,
			IList<Instruction> => TextColor.Label,
			IMethod => TextColor.InstanceMethod,
			IField => TextColor.InstanceField,
			ITypeDefOrRef => TextColor.Type,
			byte or sbyte or short or ushort or int or uint or long or ulong or float or double => TextColor.Number,
			_ => TextColor.Text,
		};

		static string FormatOperand(object? operand) {
			if (operand is null)
				return string.Empty;
			if (operand is Instruction target)
				return "IL_" + target.Offset.ToString("X4", CultureInfo.InvariantCulture);
			if (operand is IList<Instruction> targets)
				return "(" + string.Join(", ", targets.Select(item => "IL_" + item.Offset.ToString("X4", CultureInfo.InvariantCulture))) + ")";
			if (operand is string text)
				return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
			if (operand is IFormattable formattable)
				return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
			return operand.ToString() ?? string.Empty;
		}

		sealed class SnippetBuilder {
			readonly StringBuilder text = new StringBuilder();
			readonly List<ColoredTextSpan> spans = new List<ColoredTextSpan>();

			public void Append(string value, TextColor color, object? reference = null) {
				if (value.Length == 0)
					return;
				int start = text.Length;
				text.Append(value);
				spans.Add(new ColoredTextSpan(start, value.Length, color, reference));
			}

			public void AppendLine() => text.AppendLine();

			public HighlightedSnippet Create() => new HighlightedSnippet(text.ToString(), spans);
		}
	}

	sealed class ControlFlowGraphEdge {
		public ControlFlowBlock Source { get; }
		public ControlFlowBlock Target { get; }
		public ControlFlowEdgeKind Kind { get; }
		public string Label { get; }

		public ControlFlowGraphEdge(ControlFlowBlock source, ControlFlowBlock target,
			ControlFlowEdgeKind kind, string label) {
			Source = source;
			Target = target;
			Kind = kind;
			Label = label;
		}
	}

	sealed class ControlFlowGraph {
		public MethodDef Method { get; }
		public IReadOnlyList<ControlFlowBlock> Blocks { get; }
		public IReadOnlyList<ControlFlowGraphEdge> Edges { get; }
		public bool IsTruncated { get; }

		ControlFlowGraph(MethodDef method, IReadOnlyList<ControlFlowBlock> blocks,
			IReadOnlyList<ControlFlowGraphEdge> edges, bool is_truncated) {
			Method = method;
			Blocks = blocks;
			Edges = edges;
			IsTruncated = is_truncated;
		}

		public static ControlFlowGraph Build(MethodDef method, int maximum_blocks = 250) {
			if (!method.HasBody || method.Body.Instructions.Count == 0)
				return new ControlFlowGraph(method, Array.Empty<ControlFlowBlock>(), Array.Empty<ControlFlowGraphEdge>(), false);
			maximum_blocks = Math.Max(20, Math.Min(500, maximum_blocks));

			var instructions = method.Body.Instructions.ToArray();
			var instruction_indices = instructions.Select((instruction, index) => (instruction, index))
				.ToDictionary(pair => pair.instruction, pair => pair.index);
			var leaders = new HashSet<Instruction> { instructions[0] };
			for (int i = 0; i < instructions.Length; i++) {
				var instruction = instructions[i];
				AddTargets(instruction.Operand, leaders, instruction_indices);
				var flow = instruction.OpCode.FlowControl;
				if (i + 1 < instructions.Length && (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch ||
					flow == FlowControl.Return || flow == FlowControl.Throw || instruction.OpCode.Code == Code.Jmp))
					leaders.Add(instructions[i + 1]);
			}

			foreach (var handler in method.Body.ExceptionHandlers) {
				AddLeader(handler.TryStart, leaders, instruction_indices);
				AddLeader(handler.TryEnd, leaders, instruction_indices);
				AddLeader(handler.FilterStart, leaders, instruction_indices);
				AddLeader(handler.HandlerStart, leaders, instruction_indices);
				AddLeader(handler.HandlerEnd, leaders, instruction_indices);
			}

			var leader_indices = leaders.Select(item => instruction_indices[item]).Distinct().OrderBy(index => index).ToArray();
			var blocks = new List<ControlFlowBlock>(leader_indices.Length);
			var block_by_instruction = new Dictionary<Instruction, ControlFlowBlock>();
			for (int i = 0; i < leader_indices.Length; i++) {
				int start = leader_indices[i];
				int end = i + 1 < leader_indices.Length ? leader_indices[i + 1] : instructions.Length;
				var block_instructions = instructions.Skip(start).Take(end - start).ToArray();
				var block = new ControlFlowBlock(method, i, block_instructions);
				blocks.Add(block);
				foreach (var instruction in block_instructions)
					block_by_instruction[instruction] = block;
			}

			var edges = new List<ControlFlowGraphEdge>();
			var edge_keys = new HashSet<(int Source, int Target, ControlFlowEdgeKind Kind, string Label)>();
			void AddEdge(ControlFlowBlock source, ControlFlowBlock? target, ControlFlowEdgeKind kind, string label) {
				if (target is null || !edge_keys.Add((source.Index, target.Index, kind, label)))
					return;
				edges.Add(new ControlFlowGraphEdge(source, target, kind, label));
			}

			foreach (var block in blocks) {
				var last = block.Instructions[block.Instructions.Count - 1];
				var next = block.Index + 1 < blocks.Count ? blocks[block.Index + 1] : null;
				if (last.OpCode.Code == Code.Switch && last.Operand is IList<Instruction> switch_targets) {
					for (int i = 0; i < switch_targets.Count; i++)
						AddEdge(block, block_by_instruction[switch_targets[i]], ControlFlowEdgeKind.Switch,
							UiText.Pick("case ", "вариант ") + i.ToString(CultureInfo.InvariantCulture));
					AddEdge(block, next, ControlFlowEdgeKind.FallThrough, UiText.Pick("default", "иначе"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Cond_Branch) {
					if (last.Operand is Instruction conditional_target)
						AddEdge(block, block_by_instruction[conditional_target], ControlFlowEdgeKind.Conditional, UiText.Pick("branch", "переход"));
					AddEdge(block, next, ControlFlowEdgeKind.FallThrough, UiText.Pick("next", "далее"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Branch) {
					if (last.Operand is Instruction branch_target)
						AddEdge(block, block_by_instruction[branch_target], ControlFlowEdgeKind.Unconditional, string.Empty);
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Return || last.OpCode.FlowControl == FlowControl.Throw ||
					last.OpCode.Code == Code.Jmp || last.OpCode.Code == Code.Endfinally || last.OpCode.Code == Code.Endfilter)
					continue;
				AddEdge(block, next, ControlFlowEdgeKind.FallThrough, string.Empty);
			}

			foreach (var handler in method.Body.ExceptionHandlers) {
				if (handler.TryStart is null || !block_by_instruction.TryGetValue(handler.TryStart, out var try_block))
					continue;
				var destination_instruction = handler.FilterStart ?? handler.HandlerStart;
				if (destination_instruction is null || !block_by_instruction.TryGetValue(destination_instruction, out var handler_block))
					continue;
				string label = handler.HandlerType == ExceptionHandlerType.Catch && handler.CatchType is not null
					? UiText.Pick("catch ", "перехват ") + handler.CatchType.TypeName
					: handler.HandlerType.ToString().ToLowerInvariant();
				AddEdge(try_block, handler_block, ControlFlowEdgeKind.Exception, label);
			}

			bool is_truncated = blocks.Count > maximum_blocks;
			if (is_truncated)
				LimitToReachableBlocks(blocks, edges, maximum_blocks, out blocks, out edges);
			AssignLayers(blocks, edges);
			return new ControlFlowGraph(method, blocks, edges, is_truncated);
		}

		static void LimitToReachableBlocks(List<ControlFlowBlock> blocks, List<ControlFlowGraphEdge> edges,
			int maximum_blocks, out List<ControlFlowBlock> limited_blocks, out List<ControlFlowGraphEdge> limited_edges) {
			var outgoing = edges.GroupBy(edge => edge.Source)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).Distinct().ToArray());
			var selected = new HashSet<ControlFlowBlock>();
			var queue = new Queue<ControlFlowBlock>();
			if (blocks.Count != 0) {
				selected.Add(blocks[0]);
				queue.Enqueue(blocks[0]);
			}
			while (queue.Count != 0 && selected.Count < maximum_blocks) {
				var current = queue.Dequeue();
				if (!outgoing.TryGetValue(current, out var targets))
					continue;
				foreach (var target in targets.OrderBy(item => item.Index)) {
					if (selected.Count >= maximum_blocks)
						break;
					if (selected.Add(target))
						queue.Enqueue(target);
				}
			}
			foreach (var block in blocks) {
				if (selected.Count >= maximum_blocks)
					break;
				selected.Add(block);
			}
			limited_blocks = blocks.Where(selected.Contains).ToList();
			limited_edges = edges.Where(edge => selected.Contains(edge.Source) && selected.Contains(edge.Target)).ToList();
		}

		static void AddTargets(object? operand, HashSet<Instruction> leaders,
			IReadOnlyDictionary<Instruction, int> instruction_indices) {
			if (operand is Instruction target)
				AddLeader(target, leaders, instruction_indices);
			else if (operand is IList<Instruction> targets) {
				foreach (var item in targets)
					AddLeader(item, leaders, instruction_indices);
			}
		}

		static void AddLeader(Instruction? instruction, HashSet<Instruction> leaders,
			IReadOnlyDictionary<Instruction, int> instruction_indices) {
			if (instruction is not null && instruction_indices.ContainsKey(instruction))
				leaders.Add(instruction);
		}

		static void AssignLayers(IReadOnlyList<ControlFlowBlock> blocks, IReadOnlyList<ControlFlowGraphEdge> edges) {
			var outgoing = edges.GroupBy(edge => edge.Source)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).Distinct().ToArray());
			var visited = new HashSet<ControlFlowBlock>();
			int component_base = 0;
			foreach (var seed in blocks) {
				if (!visited.Add(seed))
					continue;
				seed.Layer = component_base;
				var queue = new Queue<ControlFlowBlock>();
				queue.Enqueue(seed);
				int maximum_layer = component_base;
				while (queue.Count != 0) {
					var current = queue.Dequeue();
					if (!outgoing.TryGetValue(current, out var targets))
						continue;
					foreach (var target in targets) {
						if (!visited.Add(target))
							continue;
						target.Layer = current.Layer + 1;
						maximum_layer = Math.Max(maximum_layer, target.Layer);
						queue.Enqueue(target);
					}
				}
				component_base = maximum_layer + 1;
			}
		}
	}
}
