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
	enum control_flow_edge_kind {
		fall_through,
		conditional,
		unconditional,
		@switch,
		exception,
	}

	sealed class control_flow_block {
		public MethodDef method { get; }
		public int index { get; }
		public IReadOnlyList<Instruction> instructions { get; }
		public uint start_offset => this.instructions[0].Offset;
		public uint end_offset => this.instructions[this.instructions.Count - 1].Offset;
		public int layer { get; internal set; }
		public bool is_entry => this.index == 0;
		public string title => "IL_" + this.start_offset.ToString("X4", CultureInfo.InvariantCulture);
		public string description => control_flow_block.describe_terminator(this.instructions[this.instructions.Count - 1]);
		highlighted_snippet? snippet;
		readonly CancellationToken cancellation_token;
		public highlighted_snippet snippet_value => this.snippet ??= control_flow_block.build_snippet(this.instructions, 1024, this.cancellation_token);
		public highlighted_snippet get_snippet(int maximum_lines) => maximum_lines <= 0 || maximum_lines >= 1024
			? this.snippet_value : control_flow_block.build_snippet(this.instructions, maximum_lines, this.cancellation_token);

		public control_flow_block(MethodDef method, int index, IReadOnlyList<Instruction> instructions, CancellationToken cancellation_token = default) {
			this.method = method;
			this.index = index;
			this.instructions = instructions;
			this.cancellation_token = cancellation_token;
		}

		static string describe_terminator(Instruction instruction) {
			if (instruction.OpCode.Code == Code.Switch)
				return ui_text.pick("switch", "переключатель");
			return instruction.OpCode.FlowControl switch {
				FlowControl.Cond_Branch => ui_text.pick("conditional branch", "условный переход"),
				FlowControl.Branch => ui_text.pick("unconditional branch", "безусловный переход"),
				FlowControl.Return => ui_text.pick("return", "возврат"),
				FlowControl.Throw => ui_text.pick("throw", "исключение"),
				_ => ui_text.pick("fall through", "следующий блок"),
			};
		}

		internal static highlighted_snippet build_snippet(IReadOnlyList<Instruction> instructions, int maximum_lines = 1024, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			var builder = new snippet_builder();
			int count = Math.Min(instructions.Count, Math.Clamp(maximum_lines, 1, 1024));
			int i = 0;
			for (; i < count && builder.length < 64000; i++) {
				if ((i & 31) == 0) cancellation_token.ThrowIfCancellationRequested();
				var instruction = instructions[i];
				builder.append("IL_" + instruction.Offset.ToString("X4", CultureInfo.InvariantCulture) + ":", TextColor.Label);
				builder.append("  ", TextColor.Text);
				builder.append(instruction.OpCode.Name, TextColor.OpCode);
				string operand = control_flow_block.format_operand(instruction.Operand);
				if (operand.Length != 0) {
					builder.append(" ", TextColor.Text);
					builder.append(operand, control_flow_block.operand_color(instruction.Operand), instruction.Operand is IMethod ? instruction.Operand : null);
				}
				if (i + 1 < instructions.Count)
					builder.append_line();
			}
			if (i < instructions.Count) builder.append("… " + ui_text.pick("IL preview truncated", "IL-предпросмотр сокращён"), TextColor.Gray);
			return builder.create();
		}

		static TextColor operand_color(object? operand) => operand switch {
			string => TextColor.String,
			Instruction => TextColor.Label,
			IList<Instruction> => TextColor.Label,
			IMethod => TextColor.InstanceMethod,
			IField => TextColor.InstanceField,
			ITypeDefOrRef => TextColor.Type,
			byte or sbyte or short or ushort or int or uint or long or ulong or float or double => TextColor.Number,
			_ => TextColor.Text,
		};

		static string format_operand(object? operand) {
			if (operand is null)
				return string.Empty;
			if (operand is Instruction target)
				return "IL_" + target.Offset.ToString("X4", CultureInfo.InvariantCulture);
			if (operand is IList<Instruction> targets)
				return "(" + string.Join(", ", targets.Take(64).Select(item => "IL_" + item.Offset.ToString("X4", CultureInfo.InvariantCulture))) + (targets.Count > 64 ? ", …" : string.Empty) + ")";
			if (operand is string text)
				return "\"" + control_flow_block.limit(text).Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
			if (operand is IFormattable formattable)
				return control_flow_block.limit(formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty);
			return control_flow_block.limit(operand.ToString() ?? string.Empty);
		}
		static string limit(string text) => text.Length <= 1024 ? text : text.Substring(0, 1024) + "…";

		sealed class snippet_builder {
			readonly StringBuilder text = new StringBuilder();
			readonly List<colored_text_span> spans = new List<colored_text_span>();
			public int length => this.text.Length;

			public void append(string value, TextColor color, object? reference = null) {
				if (value.Length == 0)
					return;
				int start = this.text.Length;
				this.text.Append(value);
				this.spans.Add(new colored_text_span(start, value.Length, color, reference));
			}

			public void append_line() => this.text.AppendLine();

			public highlighted_snippet create() => new highlighted_snippet(this.text.ToString(), this.spans);
		}
	}

	sealed class control_flow_graph_edge {
		public control_flow_block source { get; }
		public control_flow_block target { get; }
		public control_flow_edge_kind kind { get; }
		public string label { get; }

		public control_flow_graph_edge(control_flow_block source, control_flow_block target,
			control_flow_edge_kind kind, string label) {
			this.source = source;
			this.target = target;
			this.kind = kind;
			this.label = label;
		}
	}

	sealed class control_flow_graph {
		public static highlighted_snippet method_preview(MethodDef method, int maximum_lines) {
			if (!method.HasBody || method.Body.Instructions.Count == 0) return highlighted_snippet.plain(ui_text.no_method_body);
			int limit = maximum_lines > 0 ? Math.Min(maximum_lines, 80) : 80;
			var snippet = control_flow_block.build_snippet(method.Body.Instructions.Take(limit).ToArray());
			string heading = ui_text.preview_il_fallback + Environment.NewLine;
			return new highlighted_snippet(heading + snippet.text,
				new[] { new colored_text_span(0, heading.Length, TextColor.Comment) }
				.Concat(snippet.spans.Select(span => new colored_text_span(heading.Length + span.start, span.length, span.color, span.reference))).ToArray());
		}

		public MethodDef method { get; }
		public IReadOnlyList<control_flow_block> blocks { get; }
		public IReadOnlyList<control_flow_graph_edge> edges { get; }
		public bool is_truncated { get; }

		control_flow_graph(MethodDef method, IReadOnlyList<control_flow_block> blocks,
			IReadOnlyList<control_flow_graph_edge> edges, bool is_truncated) {
			this.method = method;
			this.blocks = blocks;
			this.edges = edges;
			this.is_truncated = is_truncated;
		}

		public static control_flow_graph build(MethodDef method, int maximum_blocks = 250, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			if (!method.HasBody || method.Body.Instructions.Count == 0)
				return new control_flow_graph(method, Array.Empty<control_flow_block>(), Array.Empty<control_flow_graph_edge>(), false);
			maximum_blocks = Math.Max(20, Math.Min(500, maximum_blocks));
			if (method.Body.Instructions.Count > 200000) throw new InvalidOperationException(ui_text.pick("Method exceeds the CFG instruction safety limit.", "Метод превышает защитный лимит IL-инструкций для CFG."));
			if (method.Body.ExceptionHandlers.Count > 128) throw new InvalidOperationException(ui_text.pick("Method exceeds the CFG exception-handler safety limit.", "Метод превышает защитный лимит обработчиков исключений CFG."));

			var instructions = method.Body.Instructions.ToArray();
			var instruction_indices = instructions.Select((instruction, index) => (instruction: instruction, index: index))
				.ToDictionary(pair => pair.instruction, pair => pair.index);
			var leaders = new HashSet<Instruction> { instructions[0] };
			long branch_targets = 0;
			for (int i = 0; i < instructions.Length; i++) {
				if ((i & 1023) == 0) cancellation_token.ThrowIfCancellationRequested();
				var instruction = instructions[i];
				if (instruction.OpCode.Code == Code.Switch && instruction.Operand is not IList<Instruction> ||
					instruction.OpCode.Code != Code.Switch && (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) && instruction.Operand is not Instruction)
					throw new InvalidOperationException(ui_text.pick("Invalid IL branch operand.", "Некорректный операнд перехода IL."));
				branch_targets += instruction.Operand is IList<Instruction> list ? list.Count : 1;
				if (branch_targets > 1000000) throw new InvalidOperationException("CFG branch target budget exceeded");
				if (instruction.Operand is Instruction destination && !instruction_indices.ContainsKey(destination) || instruction.Operand is IList<Instruction> destinations && destinations.Any(destination => !instruction_indices.ContainsKey(destination))) throw new InvalidOperationException(ui_text.pick("Invalid IL branch target.", "Некорректная цель перехода IL."));
				control_flow_graph.add_targets(instruction.Operand, leaders, instruction_indices);
				var flow = instruction.OpCode.FlowControl;
				if (i + 1 < instructions.Length && (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch ||
					flow == FlowControl.Return || flow == FlowControl.Throw || instruction.OpCode.Code == Code.Jmp))
					leaders.Add(instructions[i + 1]);
			}

			foreach (var handler in method.Body.ExceptionHandlers) {
				control_flow_graph.add_leader(handler.TryStart, leaders, instruction_indices);
				control_flow_graph.add_leader(handler.TryEnd, leaders, instruction_indices);
				control_flow_graph.add_leader(handler.FilterStart, leaders, instruction_indices);
				control_flow_graph.add_leader(handler.HandlerStart, leaders, instruction_indices);
				control_flow_graph.add_leader(handler.HandlerEnd, leaders, instruction_indices);
			}

			var leader_indices = leaders.Select(item => instruction_indices[item]).Distinct().OrderBy(index => index).ToArray();
			var blocks = new List<control_flow_block>(leader_indices.Length);
			var block_by_instruction = new Dictionary<Instruction, control_flow_block>();
			for (int i = 0; i < leader_indices.Length; i++) {
				if ((i & 127) == 0) cancellation_token.ThrowIfCancellationRequested();
				int start = leader_indices[i];
				int end = i + 1 < leader_indices.Length ? leader_indices[i + 1] : instructions.Length;
				var block_instructions = instructions.AsSpan(start, end - start).ToArray();
				var block = new control_flow_block(method, i, block_instructions, cancellation_token);
				blocks.Add(block);
				foreach (var instruction in block_instructions)
					block_by_instruction[instruction] = block;
			}

			var edges = new List<control_flow_graph_edge>();
			int position(Instruction? instruction) => instruction is null ? instructions.Length :
				instruction_indices.TryGetValue(instruction, out int position) ? position : throw new InvalidOperationException("Invalid CFG exception region boundary");
			var handlers = method.Body.ExceptionHandlers.Select(handler => (handler: handler,
start: position(handler.TryStart), end: position(handler.TryEnd), entry: position(handler.HandlerStart),
handler_end: position(handler.HandlerEnd), filter: handler.FilterStart is null ? -1 : position(handler.FilterStart))).ToArray();
			foreach (var handler in handlers)
				if (handler.start >= handler.end || handler.entry >= handler.handler_end || handler.filter >= handler.entry || handler.handler.HandlerType == ExceptionHandlerType.Filter && handler.filter < 0)
					throw new InvalidOperationException("Invalid CFG exception region range");
			var finally_exits = handlers.Where(handler => handler.handler.HandlerType == ExceptionHandlerType.Finally)
				.ToDictionary(handler => handler.handler, handler => blocks.Where(block => {
					var last = block.instructions[block.instructions.Count - 1];
					int position = instruction_indices[last];
					return last.OpCode.Code == Code.Endfinally && position >= handler.entry && position < handler.handler_end &&
						!handlers.Any(inner => inner.handler != handler.handler && inner.entry >= handler.entry && inner.handler_end <= handler.handler_end &&
							position >= inner.entry && position < inner.handler_end);
				}).ToArray());
			var edge_keys = new HashSet<(int source, int target, control_flow_edge_kind kind, string label)>();
			void add_edge(control_flow_block source, control_flow_block? target, control_flow_edge_kind kind, string label) {
				if (target is null || !edge_keys.Add((source.index, target.index, kind: kind, label: label)))
					return;
				edges.Add(new control_flow_graph_edge(source, target, kind, label));
				if (edges.Count > 1000000) throw new InvalidOperationException("CFG edge budget exceeded");
			}

			foreach (var block in blocks) {
				cancellation_token.ThrowIfCancellationRequested();
				var last = block.instructions[block.instructions.Count - 1];
				var next = block.index + 1 < blocks.Count ? blocks[block.index + 1] : null;
				if (last.OpCode.Code == Code.Switch && last.Operand is IList<Instruction> switch_targets) {
					for (int i = 0; i < switch_targets.Count; i++)
						add_edge(block, block_by_instruction.TryGetValue(switch_targets[i], out var switch_block) ? switch_block : null, control_flow_edge_kind.@switch,
							ui_text.pick("case ", "вариант ") + i.ToString(CultureInfo.InvariantCulture));
					add_edge(block, next, control_flow_edge_kind.fall_through, ui_text.pick("default", "иначе"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Cond_Branch) {
					if (last.Operand is Instruction conditional_target)
						add_edge(block, block_by_instruction.TryGetValue(conditional_target, out var conditional_block) ? conditional_block : null, control_flow_edge_kind.conditional, ui_text.pick("branch", "переход"));
					add_edge(block, next, control_flow_edge_kind.fall_through, ui_text.pick("next", "далее"));
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Branch) {
					if (last.Operand is Instruction branch_target) {
						var branch_block = block_by_instruction[branch_target];
						int source_position = instruction_indices[last], destination_position = instruction_indices[branch_target];
						var finalizers = last.OpCode.Code is Code.Leave or Code.Leave_S ? handlers.Where(handler =>
							handler.handler.HandlerType == ExceptionHandlerType.Finally && source_position >= handler.start && source_position < handler.end &&
							(destination_position < handler.start || destination_position >= handler.end)).OrderBy(handler => handler.end - handler.start).ToArray() : Array.Empty<(ExceptionHandler handler, int start, int end, int entry, int handler_end, int filter)>();
						add_edge(block, finalizers.Length == 0 ? branch_block : block_by_instruction[instructions[finalizers[0].entry]], control_flow_edge_kind.unconditional, finalizers.Length == 0 ? string.Empty : "leave / finally");
						for (int i = 0; i < finalizers.Length; i++) {
							var continuation = i + 1 < finalizers.Length ? block_by_instruction[instructions[finalizers[i + 1].entry]] : branch_block;
							foreach (var exit in finally_exits[finalizers[i].handler])
								add_edge(exit, continuation, control_flow_edge_kind.unconditional, "leave → " + branch_block.title);
						}
					}
					continue;
				}
				if (last.OpCode.FlowControl == FlowControl.Return || last.OpCode.FlowControl == FlowControl.Throw ||
					last.OpCode.Code == Code.Jmp || last.OpCode.Code == Code.Endfinally || last.OpCode.Code == Code.Endfilter)
					continue;
				add_edge(block, next, control_flow_edge_kind.fall_through, string.Empty);
			}

			foreach (var handler in handlers) {
				cancellation_token.ThrowIfCancellationRequested();
				var handler_block = block_by_instruction[instructions[handler.filter >= 0 ? handler.filter : handler.entry]];
				string label = handler.handler.HandlerType == ExceptionHandlerType.Catch && handler.handler.CatchType is not null
					? ui_text.pick("catch ", "перехват ") + handler.handler.CatchType.TypeName
					: handler.handler.HandlerType.ToString().ToLowerInvariant();
				foreach (var source in blocks) {
					cancellation_token.ThrowIfCancellationRequested();
					if (source.instructions.Any(instruction => instruction_indices[instruction] >= handler.start && instruction_indices[instruction] < handler.end && method_flow_runner.may_throw(instruction)))
						add_edge(source, handler_block, control_flow_edge_kind.exception, label);
					if (handler.filter >= 0 && source.instructions.Last().OpCode.Code == Code.Endfilter &&
						instruction_indices[source.instructions.Last()] >= handler.filter && instruction_indices[source.instructions.Last()] < handler.entry)
						add_edge(source, block_by_instruction[instructions[handler.entry]], control_flow_edge_kind.conditional, "filter accepted");
				}
			}

			bool is_truncated = blocks.Count > maximum_blocks;
			if (is_truncated)
				control_flow_graph.limit_to_reachable_blocks(blocks, edges, maximum_blocks, cancellation_token, out blocks, out edges);
			control_flow_graph.assign_layers(blocks, edges, cancellation_token);
			return new control_flow_graph(method, blocks, edges, is_truncated);
		}

		static void limit_to_reachable_blocks(List<control_flow_block> blocks, List<control_flow_graph_edge> edges,
			int maximum_blocks, CancellationToken cancellation_token, out List<control_flow_block> limited_blocks, out List<control_flow_graph_edge> limited_edges) {
			var outgoing = edges.GroupBy(edge => edge.source)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.target).Distinct().ToArray());
			var selected = new HashSet<control_flow_block>();
			var queue = new Queue<control_flow_block>();
			if (blocks.Count != 0) {
				selected.Add(blocks[0]);
				queue.Enqueue(blocks[0]);
			}
			while (queue.Count != 0 && selected.Count < maximum_blocks) {
				cancellation_token.ThrowIfCancellationRequested();
				var current = queue.Dequeue();
				if (!outgoing.TryGetValue(current, out var targets))
					continue;
				foreach (var target in targets.OrderBy(item => item.index)) {
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
			limited_edges = edges.Where(edge => selected.Contains(edge.source) && selected.Contains(edge.target)).ToList();
		}

		static void add_targets(object? operand, HashSet<Instruction> leaders,
			IReadOnlyDictionary<Instruction, int> instruction_indices) {
			if (operand is Instruction target)
				control_flow_graph.add_leader(target, leaders, instruction_indices);
			else if (operand is IList<Instruction> targets) {
				foreach (var item in targets)
					control_flow_graph.add_leader(item, leaders, instruction_indices);
			}
		}

		static void add_leader(Instruction? instruction, HashSet<Instruction> leaders,
			IReadOnlyDictionary<Instruction, int> instruction_indices) {
			if (instruction is not null && instruction_indices.ContainsKey(instruction))
				leaders.Add(instruction);
		}

		static void assign_layers(IReadOnlyList<control_flow_block> blocks, IReadOnlyList<control_flow_graph_edge> edges, CancellationToken cancellation_token) {
			var outgoing = edges.GroupBy(edge => edge.source)
				.ToDictionary(group => group.Key, group => group.Select(edge => edge.target).Distinct().ToArray());
			var visited = new HashSet<control_flow_block>();
			int component_base = 0;
			foreach (var seed in blocks) {
				if (!visited.Add(seed))
					continue;
				seed.layer = component_base;
				var queue = new Queue<control_flow_block>();
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
						target.layer = current.layer + 1;
						maximum_layer = Math.Max(maximum_layer, target.layer);
						queue.Enqueue(target);
					}
				}
				component_base = maximum_layer + 1;
			}
		}
	}
}
