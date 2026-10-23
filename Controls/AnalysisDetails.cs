using System.Linq;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

namespace dnSpy.CallGraph.Controls {
	static class AnalysisDetails {
		public static bool IsWarning(GraphDiagnostic item) => item.Kind is GraphDiagnosticKind.UnresolvedPointer or GraphDiagnosticKind.UnresolvedDelegate or GraphDiagnosticKind.UnresolvedReflection or GraphDiagnosticKind.UnresolvedReference or GraphDiagnosticKind.AnalysisBudget or GraphDiagnosticKind.UnsupportedInstruction or GraphDiagnosticKind.InvalidIl;
		public static string Summary(CallGraphSlice? slice, MethodDef method) {
			int warnings = slice?.DiagnosticsFor(method).Count(IsWarning) ?? 0;
			return warnings == 0 ? string.Empty : UiText.Pick($"Analysis notes: {warnings}", $"Замечаний анализа: {warnings}");
		}
		public static string Describe(GraphDiagnostic item) {
			string label = item.Kind switch {
				GraphDiagnosticKind.AddressReference => UiText.Pick("Method reference, not a call", "Ссылка на метод, не вызов"),
				GraphDiagnosticKind.UnresolvedPointer => UiText.Pick("Unresolved function pointer", "Не определён адрес вызываемого метода"),
				GraphDiagnosticKind.UnresolvedDelegate => UiText.Pick("Unresolved delegate/dynamic target", "Не определена цель делегата/dynamic"),
				GraphDiagnosticKind.UnresolvedReflection => UiText.Pick("Unresolved reflection target", "Не определена цель reflection"),
				GraphDiagnosticKind.AnalysisBudget => UiText.Pick("Analysis stopped at its work limit", "Анализ участка остановлен по лимиту работы"),
				GraphDiagnosticKind.UnsupportedInstruction => UiText.Pick("IL semantics not fully modeled", "Семантика IL учтена не полностью"),
				GraphDiagnosticKind.InvalidIl => UiText.Pick("Invalid or unsupported IL structure", "Некорректная или неподдерживаемая структура IL"),
				GraphDiagnosticKind.NativeBoundary => UiText.Pick("Native call boundary", "Граница вызова нативной функции"),
				GraphDiagnosticKind.RuntimeSelection => UiText.Pick("Runtime selection among possible targets", "Выбор одной из возможных целей при выполнении"),
				_ => UiText.Pick("Method reference could not be resolved", "Не удалось разрешить ссылку на метод"),
			};
			string detail = item.Detail switch {
				"value-flow incomplete; structurally reachable calls retained as possible targets" => UiText.Pick(item.Detail, "Расчёт значений не завершён; достижимые по IL вызовы сохранены как возможные"),
				"instruction budget exhausted" => UiText.Pick(item.Detail, "Достигнут лимит обработки IL-инструкций"),
				"graph analysis budget exhausted" => UiText.Pick(item.Detail, "Достигнут общий лимит работы анализатора"),
				"switch selector is not known in this context" => UiText.Pick(item.Detail, "Не удалось вычислить значение switch в этом контексте"),
				"branch condition is not known in this context" => UiText.Pick(item.Detail, "Не удалось вычислить условие ветвления в этом контексте"),
				"function pointer origin is not known in this context" => UiText.Pick(item.Detail, "Происхождение указателя в этом контексте неизвестно"),
				"invocation target depends on unresolved values" => UiText.Pick(item.Detail, "Цель вызова зависит от неразрешённых значений"),
				"function pointer signature or remaining targets could not be resolved" => UiText.Pick(item.Detail, "Не удалось сопоставить сигнатуру указателя или определить остальные цели"),
				_ => item.Detail,
			};
			return $"IL_{item.Offset:X4}: {label}" + (item.Target is null ? " — " + detail : $" — {detail} → 0x{item.Target.MDToken.Raw:X8} {CallGraphIndex.DisplayName(item.Target)}");
		}
		public static string ToolTip(CallGraphSlice slice, MethodDef method) {
			var details = slice.DiagnosticsFor(method);
			return details.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", details.OrderByDescending(IsWarning).Take(12).Select(Describe));
		}
		public static string Status(CallGraphSlice slice) {
			int count = slice.Diagnostics.Count(IsWarning);
			return count == 0 ? string.Empty : UiText.Pick($"   •   analysis notes: {count}", $"   •   замечаний анализа: {count}");
		}
		public static string StatusToolTip(CallGraphSlice slice) => UiText.Pick("Static call graph, not an execution trace. Address references are separate from calls.", "Статический граф вызовов, не трасса выполнения. Ссылки на адреса методов отделены от вызовов.") + "\n\n" + string.Join("\n", slice.Diagnostics.Where(IsWarning).Take(60).Select(item => $"0x{item.Method.MDToken.Raw:X8} " + Describe(item)));
	}
}
