using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
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
		HighlightedSnippet? snippet;
		readonly CancellationToken cancellation_token;
		public HighlightedSnippet Snippet => snippet ??= BuildSnippet(Instructions, 1024, cancellation_token);
		public HighlightedSnippet GetSnippet(int maximum_lines) => maximum_lines <= 0 || maximum_lines >= 1024
			? Snippet : BuildSnippet(Instructions, maximum_lines, cancellation_token);

		public ControlFlowBlock(MethodDef method, int index, IReadOnlyList<Instruction> instructions, CancellationToken cancellation_token = default) {
			Method = method;
			Index = index;
			Instructions = instructions;
			this.cancellation_token = cancellation_token;
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

		internal static HighlightedSnippet BuildSnippet(IReadOnlyList<Instruction> instructions, int maximum_lines = 1024, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			var builder = new SnippetBuilder();
			int count = Math.Min(instructions.Count, Math.Clamp(maximum_lines, 1, 1024));
			int i = 0;
			for (; i < count && builder.Length < 64000; i++) {
				if ((i & 31) == 0) cancellation_token.ThrowIfCancellationRequested();
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
			if (i < instructions.Count) builder.Append("… " + UiText.Pick("IL preview truncated", "IL-предпросмотр сокращён"), TextColor.Gray);
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
				return "(" + string.Join(", ", targets.Take(64).Select(item => "IL_" + item.Offset.ToString("X4", CultureInfo.InvariantCulture))) + (targets.Count > 64 ? ", …" : string.Empty) + ")";
			if (operand is string text)
				return "\"" + Limit(text).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
			if (operand is IFormattable formattable)
				return Limit(formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty);
			return Limit(operand.ToString() ?? string.Empty);
		}
		static string Limit(string text) => text.Length <= 1024 ? text : text.Substring(0, 1024) + "…";

		sealed class SnippetBuilder {
			readonly StringBuilder text = new StringBuilder();
			readonly List<ColoredTextSpan> spans = new List<ColoredTextSpan>();
			public int Length => text.Length;

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
		public static HighlightedSnippet MethodPreview(MethodDef method, int maximum_lines) {
			if (!method.HasBody || method.Body.Instructions.Count == 0) return HighlightedSnippet.Plain(UiText.NoMethodBody);
			int limit = maximum_lines > 0 ? Math.Min(maximum_lines, 80) : 80;
			var snippet = ControlFlowBlock.BuildSnippet(method.Body.Instructions.Take(limit).ToArray());
			string heading = UiText.PreviewIlFallback + Environment.NewLine;
			return new HighlightedSnippet(heading + snippet.Text,
				new[] { new ColoredTextSpan(0, heading.Length, TextColor.Comment) }
				.Concat(snippet.Spans.Select(span => new ColoredTextSpan(heading.Length + span.Start, span.Length, span.Color, span.Reference))).ToArray());
		}

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

		public static ControlFlowGraph Build(MethodDef method, int maximum_blocks = 250, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			if (!method.HasBody || method.Body.Instructions.Count == 0)
				return new ControlFlowGraph(method, Array.Empty<ControlFlowBlock>(), Array.Empty<ControlFlowGraphEdge>(), false);
			maximum_blocks = Math.Max(20, Math.Min(500, maximum_blocks));
			if (method.Body.Instructions.Count > 200000) throw new InvalidOperationException(UiText.Pick("Method exceeds the CFG instruction safety limit.", "Метод превышает защитный лимит IL-инструкций для CFG."));
			if (method.Body.ExceptionHandlers.Count > 128) throw new InvalidOperationException(UiText.Pick("Method exceeds the CFG exception-handler safety limit.", "Метод превышает защитный лимит обработчиков исключений CFG."));

			var instructions = method.Body.Instructions.ToArray();
			var instruction_indices = instructions.Select((instruction, index) => (instruction, index))
				.ToDictionary(pair => pair.instruction, pair => pair.index);
			var leaders = new HashSet<Instruction> { instructions[0] };
			long branch_targets = 0;
			for (int i = 0; i < instructions.Length; i++) {
				if ((i & 1023) == 0) cancellation_token.ThrowIfCancellationRequested();
				var instruction = instructions[i];
				if (instruction.OpCode.Code == Code.Switch && instruction.Operand is not IList<Instruction> ||
					instruction.OpCode.Code != Code.Switch && (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) && instruction.Operand is not Instruction)
					throw new InvalidOperationException(UiText.Pick("Invalid IL branch operand.", "Некорректный операнд перехода IL."));
				branch_targets += instruction.Operand is IList<Instruction> list ? list.Count : 1;
				if (branch_targets > 1000000) throw new InvalidOperationException("CFG branch target budget exceeded");
				if (instruction.Operand is Instruction destination && !instruction_indices.ContainsKey(destination) || instruction.Operand is IList<Instruction> destinations && destinations.Any(destination => !instruction_indices.ContainsKey(destination))) throw new InvalidOperationException(UiText.Pick("Invalid IL branch target.", "Некорректная цель перехода IL."));
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
				if ((i & 127) == 0) cancellation_token.ThrowIfCancellationRequested();
				int start = leader_indices[i];
				int end = i + 1 < leader_indices.Length ? leader_indices[i + 1] : instructions.Length;
				var block_instructions = instructions.AsSpan(start, end - start).ToArray();
				var block = new ControlFlowBlock(method, i, block_instructions, cancellation_token);
				blocks.Add(block);
				foreach (var instruction in block_instructions)
					block_by_instruction[instruction] = block;
			}

			var edges = new List<ControlFlowGraphEdge>();
			int Position(Instruction? instruction) => instruction is null ? instructions.Length :
				instruction_indices.TryGetValue(instruction, out int position) ? position : throw new InvalidOperationException("Invalid CFG exception region boundary");
			var handlers = method.Body.ExceptionHandlers.Select(handler => (Handler: handler,
				Start: Position(handler.TryStart), End: Position(handler.TryEnd), Entry: Position(handler.HandlerStart),
				HandlerEnd: Position(handler.HandlerEnd), Filter: handler.FilterStart is null ? -1 : Position(handler.FilterStart))).ToArray();
			foreach (var handler in handlers)
				if (handler.Start >= handler.End || handler.Entry >= handler.HandlerEnd || handler.Filter >= handler.Entry || handler.Handler.HandlerType == ExceptionHandlerType.Filter && handler.Filter < 0)
					throw new InvalidOperationException("Invalid CFG exception region range");
			var finally_exits = handlers.Where(handler => handler.Handler.HandlerType == ExceptionHandlerType.Finally)
				.ToDictionary(handler => handler.Handler, handler => blocks.Where(block => {
					var last = block.Instructions[block.Instructions.Count - 1];
					int position = instruction_indices[last];
					return last.OpCode.Code == Code.Endfinally && position >= handler.Entry && position < handler.HandlerEnd &&
						!handlers.Any(inner => inner.Handler != handler.Handler && inner.Entry >= handler.Entry && inner.HandlerEnd <= handler.HandlerEnd &&
							position >= inner.Entry && position < inner.HandlerEnd);
				}).ToArray());
			var edge_keys = new HashSet<(int Source, int Target, ControlFlowEdgeKind Kind, string Label)>();
			void AddEdge(ControlFlowBlock source, ControlFlowBlock? target, ControlFlowEdgeKind kind, string label) {
				if (target is null || !edge_keys.Add((source.Index, target.Index, kind, label)))
					return;
				edges.Add(new ControlFlowGraphEdge(source, target, kind, label));
				if (edges.Count > 1000000) throw new InvalidOperationException("CFG edge budget exceeded");
			}

			foreach (var block in blocks) {
				cancellation_token.ThrowIfCancellationRequested();
				var last = block.Instructions[block.Instructions.Count - 1];
				var next = block.Index + 1 < blocks.Count ? blocks[block.Index + 1] : null;
				if (last.OpCode.Code == Code.Switch && last.Operand is IList<Instruction> switch_targets) {
					for (int i = 0; i < switch_targets.Count; i++)
						AddEdge(block, block_by_instruction.TryGetValue(switch_targets[i], out var switch_block) ? switch_block : null, ControlFlowEdgeKind.Switch,
							UiText.Pick("case ", "вариант ") + i.ToString(CultureInfo.InvariantCulture));
					AddEdge(block, next, ControlFlowEdgeKind.FallThrough, UiText.Pick("default", "иначе"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Cond_Branch) {
					if (last.Operand is Instruction conditional_target)
						AddEdge(block, block_by_instruction.TryGetValue(conditional_target, out var conditional_block) ? conditional_block : null, ControlFlowEdgeKind.Conditional, UiText.Pick("branch", "переход"));
					AddEdge(block, next, ControlFlowEdgeKind.FallThrough, UiText.Pick("next", "далее"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Branch) {
					if (last.Operand is Instruction branch_target) {
						var branch_block = block_by_instruction[branch_target];
						int source_position = instruction_indices[last], destination_position = instruction_indices[branch_target];
						var finalizers = last.OpCode.Code is Code.Leave or Code.Leave_S ? handlers.Where(handler =>
							handler.Handler.HandlerType == ExceptionHandlerType.Finally && source_position >= handler.Start && source_position < handler.End &&
							(destination_position < handler.Start || destination_position >= handler.End)).OrderBy(handler => handler.End - handler.Start).ToArray() : Array.Empty<(ExceptionHandler Handler, int Start, int End, int Entry, int HandlerEnd, int Filter)>();
						AddEdge(block, finalizers.Length == 0 ? branch_block : block_by_instruction[instructions[finalizers[0].Entry]], ControlFlowEdgeKind.Unconditional, finalizers.Length == 0 ? string.Empty : "leave / finally");
						for (int i = 0; i < finalizers.Length; i++) {
							var continuation = i + 1 < finalizers.Length ? block_by_instruction[instructions[finalizers[i + 1].Entry]] : branch_block;
							foreach (var exit in finally_exits[finalizers[i].Handler])
								AddEdge(exit, continuation, ControlFlowEdgeKind.Unconditional, "leave → " + branch_block.Title);
						}
					}
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Return || last.OpCode.FlowControl == FlowControl.Throw ||
					last.OpCode.Code == Code.Jmp || last.OpCode.Code == Code.Endfinally || last.OpCode.Code == Code.Endfilter)
					continue;
				AddEdge(block, next, ControlFlowEdgeKind.FallThrough, string.Empty);
			}

			foreach (var handler in handlers) {
				cancellation_token.ThrowIfCancellationRequested();
				var handler_block = block_by_instruction[instructions[handler.Filter >= 0 ? handler.Filter : handler.Entry]];
				string label = handler.Handler.HandlerType == ExceptionHandlerType.Catch && handler.Handler.CatchType is not null
					? UiText.Pick("catch ", "перехват ") + handler.Handler.CatchType.TypeName
					: handler.Handler.HandlerType.ToString().ToLowerInvariant();
				foreach (var source in blocks) {
					cancellation_token.ThrowIfCancellationRequested();
					if (source.Instructions.Any(instruction => instruction_indices[instruction] >= handler.Start && instruction_indices[instruction] < handler.End && MethodFlowRunner.MayThrow(instruction)))
						AddEdge(source, handler_block, ControlFlowEdgeKind.Exception, label);
					if (handler.Filter >= 0 && source.Instructions.Last().OpCode.Code == Code.Endfilter &&
						instruction_indices[source.Instructions.Last()] >= handler.Filter && instruction_indices[source.Instructions.Last()] < handler.Entry)
						AddEdge(source, block_by_instruction[instructions[handler.Entry]], ControlFlowEdgeKind.Conditional, "filter accepted");
				}
			}

			bool is_truncated = blocks.Count > maximum_blocks;
			if (is_truncated)
				LimitToReachableBlocks(blocks, edges, maximum_blocks, cancellation_token, out blocks, out edges);
			AssignLayers(blocks, edges, cancellation_token);
			return new ControlFlowGraph(method, blocks, edges, is_truncated);
		}

		static void LimitToReachableBlocks(List<ControlFlowBlock> blocks, List<ControlFlowGraphEdge> edges,
			int maximum_blocks, CancellationToken cancellation_token, out List<ControlFlowBlock> limited_blocks, out List<ControlFlowGraphEdge> limited_edges) {
			var outgoing = edges.GroupBy(edge => edge.Source)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).Distinct().ToArray());
			var selected = new HashSet<ControlFlowBlock>();
			var queue = new Queue<ControlFlowBlock>();
			if (blocks.Count != 0) {
				selected.Add(blocks[0]);
				queue.Enqueue(blocks[0]);
			}
			while (queue.Count != 0 && selected.Count < maximum_blocks) {
				cancellation_token.ThrowIfCancellationRequested();
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

		static void AssignLayers(IReadOnlyList<ControlFlowBlock> blocks, IReadOnlyList<ControlFlowGraphEdge> edges, CancellationToken cancellation_token) {
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
					cancellation_token.ThrowIfCancellationRequested();
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
