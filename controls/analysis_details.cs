using System.Linq;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	static class analysis_details {
		public static bool is_warning(graph_diagnostic item) => item.kind is graph_diagnostic_kind.unresolved_pointer or graph_diagnostic_kind.unresolved_delegate or graph_diagnostic_kind.unresolved_reflection or graph_diagnostic_kind.unresolved_reference or graph_diagnostic_kind.analysis_budget or graph_diagnostic_kind.unsupported_instruction or graph_diagnostic_kind.invalid_il or graph_diagnostic_kind.native_boundary;
		public static string summary(call_graph_slice? slice, MethodDef method) {
			int warnings = slice?.diagnostics_for(method).Count(analysis_details.is_warning) ?? 0;
			return warnings == 0 ? string.Empty : ui_text.pick($"Analysis notes: {warnings}", $"Замечаний анализа: {warnings}");
		}
		public static string describe(graph_diagnostic item) {
			string label = item.kind switch {
				graph_diagnostic_kind.address_reference => ui_text.pick("Method reference, not a call", "Ссылка на метод, не вызов"),
				graph_diagnostic_kind.unresolved_pointer => ui_text.pick("Unresolved function pointer", "Не определён адрес вызываемого метода"),
				graph_diagnostic_kind.unresolved_delegate => ui_text.pick("Unresolved delegate/dynamic target", "Не определена цель делегата/dynamic"),
				graph_diagnostic_kind.unresolved_reflection => ui_text.pick("Unresolved reflection target", "Не определена цель reflection"),
				graph_diagnostic_kind.analysis_budget => ui_text.pick("Analysis stopped at its work limit", "Анализ участка остановлен по лимиту работы"),
				graph_diagnostic_kind.unsupported_instruction => ui_text.pick("IL semantics not fully modeled", "Семантика IL учтена не полностью"),
				graph_diagnostic_kind.invalid_il => ui_text.pick("Invalid or unsupported IL structure", "Некорректная или неподдерживаемая структура IL"),
				graph_diagnostic_kind.native_boundary => ui_text.pick("Native call boundary", "Граница вызова нативной функции"),
				graph_diagnostic_kind.runtime_selection => ui_text.pick("Runtime selection among possible targets", "Выбор одной из возможных целей при выполнении"),
				graph_diagnostic_kind.structural_call => ui_text.pick("IL call references retained", "Ссылки вызовов из IL сохранены"),
				graph_diagnostic_kind.view_depth => ui_text.pick("View depth boundary", "Граница глубины просмотра"),
				graph_diagnostic_kind.node_limit => ui_text.pick("Visible card limit", "Лимит видимых карточек"),
				graph_diagnostic_kind.filtered_target => ui_text.pick("Hidden by display filters", "Скрыто фильтрами отображения"),
				graph_diagnostic_kind.assembly_boundary => ui_text.pick("Assembly traversal boundary", "Граница обхода сборки"),
				graph_diagnostic_kind.workspace_boundary => ui_text.pick("Outside loaded workspace", "Вне загруженного workspace"),
				graph_diagnostic_kind.refinement_progress => ui_text.pick("Saved analysis progress", "Сохранённый прогресс анализа"),
				_ => ui_text.pick("Method reference could not be resolved", "Не удалось разрешить ссылку на метод"),
			};
			string detail = item.detail switch {
				"IL operands retained independently of inferred execution paths" => ui_text.pick(item.detail, "Вызовы из IL сохранены независимо от вычисленного пути выполнения"),
				"value-flow incomplete; structurally reachable calls retained as possible targets" => ui_text.pick(item.detail, "Расчёт значений не завершён; достижимые по IL вызовы сохранены как возможные"),
				"instruction budget exhausted" => ui_text.pick(item.detail, "Достигнут лимит обработки IL-инструкций"),
				"graph analysis budget exhausted" => ui_text.pick(item.detail, "Достигнут общий лимит работы анализатора"),
				"switch selector is not known in this context" => ui_text.pick(item.detail, "Не удалось вычислить значение switch в этом контексте"),
				"branch condition is not known in this context" => ui_text.pick(item.detail, "Не удалось вычислить условие ветвления в этом контексте"),
				"function pointer origin is not known in this context" => ui_text.pick(item.detail, "Происхождение указателя в этом контексте неизвестно"),
				"invocation target depends on unresolved values" => ui_text.pick(item.detail, "Цель вызова зависит от неразрешённых значений"),
				"function pointer signature or remaining targets could not be resolved" => ui_text.pick(item.detail, "Не удалось сопоставить сигнатуру указателя или определить остальные цели"),
				_ => item.detail,
			};
			return $"IL_{item.offset:X4}: {label}" + (item.target is null ? " — " + detail : $" — {detail} → 0x{item.target.MDToken.Raw:X8} {call_graph_index.display_name(item.target)}");
		}
		public static string tool_tip(call_graph_slice slice, MethodDef method) {
			var details = slice.diagnostics_for(method);
			return details.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", details.OrderByDescending(analysis_details.is_warning).Take(12).Select(analysis_details.describe));
		}
		public static string status(call_graph_slice slice) {
			int count = slice.diagnostics.Count(analysis_details.is_warning);
			string budget = slice.can_continue_refinement ? ui_text.pick("   •   PAUSED — Continue refinement", "   •   ПАУЗА — «Уточнить дальше»") :
				slice.diagnostics.Any(item => item.kind == graph_diagnostic_kind.analysis_budget) ? ui_text.pick("   •   analysis incomplete (see reasons)", "   •   анализ неполный (см. причины)") : string.Empty;
			int view = slice.diagnostics.Count(item => item.kind is graph_diagnostic_kind.view_depth or graph_diagnostic_kind.node_limit or graph_diagnostic_kind.filtered_target or graph_diagnostic_kind.assembly_boundary or graph_diagnostic_kind.workspace_boundary);
			return budget + (view == 0 ? string.Empty : ui_text.pick($"   •   view boundaries: {view}", $"   •   границ просмотра: {view}")) + (count == 0 ? string.Empty : ui_text.pick($"   •   analysis notes: {count}", $"   •   замечаний анализа: {count}"));
		}
		public static string status_tool_tip(call_graph_slice slice) => ui_text.pick("Static graph, not an execution trace. IL = operand; ? = candidate; ldftn / & = address, not invocation. Refinement resumes its worklist; interrupted helper evaluations may be retried. Native/runtime-only targets require additional evidence.", "Статический граф, не трасса. IL = операнд; ? = кандидат; ldftn / & = адрес, не вызов. Уточнение продолжает очередь; прерванные вычисления помощников могут повторяться. Для native/runtime-целей нужны дополнительные данные.") + "\n\n" + string.Join("\n", slice.diagnostics.Where(item => item.kind != graph_diagnostic_kind.address_reference).Take(60).Select(item => $"0x{item.method.MDToken.Raw:X8} " + analysis_details.describe(item)));
	}
}
