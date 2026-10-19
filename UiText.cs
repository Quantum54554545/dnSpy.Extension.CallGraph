using System;
using System.Globalization;

namespace dnSpy.CallGraph {
		static class UiText {
		static bool Russian => string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);
		public static string Pick(string english, string russian) => Russian ? russian : english;

		public static string CallGraph => Pick("Call Graph", "Граф вызовов");
		public static string ShowInCallGraph => Pick("Show in Call Graph", "Показать в графе вызовов");
		public static string PreviousRoot => Pick("Previous graph root", "Предыдущий корень графа");
		public static string NextRoot => Pick("Next graph root", "Следующий корень графа");
		public static string CurrentSelection => Pick("Current selection", "Текущий метод");
		public static string BuildCurrentSelection => Pick("Build the selected view for the current method", "Построить выбранный граф для текущего метода");
		public static string Graph => Pick("Graph", "Граф");
		public static string Calls => Pick("Calls", "Вызовы");
		public static string Depth => Pick("Depth", "Глубина");
		public static string Limit => Pick("Limit", "Лимит");
		public static string Code => Pick("Code", "Код");
		public static string CenterRoot => Pick("Center root", "Корень в центр");
		public static string CenterRootTip => Pick("Center the current root block", "Переместить текущий корневой блок в центр");
		public static string Search => Pick("Search", "Поиск");
		public static string SearchMethodTip => Pick("Find a visible method by name, type, or signature. Enter: next; Shift+Enter: previous.", "Найти видимый метод по имени, типу или сигнатуре. Enter: следующий; Shift+Enter: предыдущий.");
		public static string PreviousMatch => Pick("Previous match", "Предыдущее совпадение");
		public static string NextMatch => Pick("Next match", "Следующее совпадение");
		public static string ClearSearch => Pick("Clear search", "Очистить поиск");
		public static string SearchUnavailableInCfg => Pick("Method search is available in Call blocks and Function map.", "Поиск методов доступен в «Блоках вызовов» и «Карте функций».");
		public static string SearchResults(int current, int total) => total == 0 ? Pick("No matches", "Нет совпадений") : $"{current} / {total}";
		public static string CallBlocks => Pick("Call blocks", "Блоки вызовов");
		public static string FunctionMap => Pick("Function map", "Карта функций");
		public static string Callees => Pick("Callees", "Вызываемые");
		public static string Callers => Pick("Callers", "Вызывающие");
		public static string Both => Pick("Both", "Оба");
		public static string ViewModeTip => Pick("Call blocks: method code\nFunction map: fast overview\nCFG: IL basic blocks inside one method", "Блоки вызовов: код методов\nКарта функций: быстрый обзор\nCFG: базовые IL-блоки одного метода");
		public static string DirectionTip => Pick("Callees: methods called by the root\nCallers: methods that call the root\nBoth: both directions", "Вызываемые: методы, которые вызывает корень\nВызывающие: методы, вызывающие корень\nОба: оба направления");
		public static string NodeLimitTip => Pick("Maximum number of visible methods or CFG blocks", "Максимальное число видимых методов или CFG-блоков");
		public static string LanguageTip => Pick("Decompiler used inside graph blocks", "Декомпилятор для кода внутри блоков");
		public static string GraphGestureTip => Pick("Drag to pan • Mouse wheel to zoom • Double-click to explore", "Перетаскивание: панорама • Колесо: масштаб • Двойной щелчок: исследовать");

		public static string SettingsHeading => Pick("Call Graph blocks", "Блоки графа вызовов");
		public static string LinesTitle => Pick("Lines shown in each block", "Строк в каждом блоке");
		public static string LinesDescription => Pick("Auto keeps normal methods complete and caps very large methods at 80 lines.", "Авто показывает обычные методы целиком, а очень большие ограничивает 80 строками.");
		public static string AutoLines => Pick("Auto (up to 80)", "Авто (до 80)");
		public static string BlockWidth => Pick("Block width", "Ширина блока");
		public static string BlockWidthDescription => Pick("Wider blocks reduce wrapping but use more horizontal space.", "Широкие блоки уменьшают перенос строк, но занимают больше места по горизонтали.");
		public static string WrapLines => Pick("Wrap long code lines", "Переносить длинные строки кода");
		public static string SyntaxHighlighting => Pick("Use dnSpy syntax highlighting", "Использовать подсветку синтаксиса dnSpy");
		public static string GraphFilters => Pick("Graph filters", "Фильтры графа");
		public static string GraphFiltersDescription => Pick("Filters apply to Call blocks and Function map. The root method is always kept visible.", "Фильтры применяются к блокам вызовов и карте функций. Корневой метод всегда остаётся видимым.");
		public static string HideSystemMethods => Pick("Hide System.* and Microsoft.* methods", "Скрывать методы System.* и Microsoft.*");
		public static string HideAccessors => Pick("Hide property and event accessors", "Скрывать аксессоры свойств и событий");
		public static string HideConstructors => Pick("Hide constructors", "Скрывать конструкторы");
		public static string OnlyCurrentNamespace => Pick("Only the current namespace", "Только текущее пространство имён");
		public static string OnlyCurrentType => Pick("Only the current type", "Только текущий тип");
		public static string RequireMethodBody => Pick("Only methods with an IL body", "Только методы с IL-телом");
		public static string MinimumCallSites => Pick("Minimum call sites per edge", "Минимум мест вызова для связи");
		public static string KeyboardShortcut => Pick("Keyboard shortcut", "Горячая клавиша");
		public static string EnableShortcut => Pick("Enable the Call Graph shortcut", "Включить горячую клавишу графа вызовов");
		public static string ShortcutDescription => Pick("The new shortcut is active as soon as Options is applied. At least one modifier is required; Ctrl is used if none is selected.", "Новая комбинация действует сразу после применения параметров. Требуется хотя бы один модификатор; если ничего не выбрано, используется Ctrl.");
		public static string AssemblyExplorer => Pick("Assembly Explorer", "Обозреватель сборок");
		public static string CleanExplorer => Pick("On launch, clear restored assemblies and keep only command-line files", "При запуске очищать восстановленные сборки и оставлять только файлы из командной строки");
		public static string CleanExplorerDescription => Pick("When enabled, dnSpy closes restored document tabs and removes assemblies from the previous session. This also applies when files are sent to an already running dnSpy instance.", "dnSpy закроет восстановленные вкладки и удалит сборки из прошлого сеанса. Это также применяется при передаче файлов в уже запущенный dnSpy.");

		public static string ExploreHere => Pick("_Explore from here", "_Исследовать отсюда");
		public static string ShowMethodInCfg => Pick("Show method in _CFG", "Показать метод в _CFG");
		public static string OpenSource => Pick("Open source in _new tab", "Открыть код в _новой вкладке");
		public static string OpenMethodSource => Pick("Open method source in _new tab", "Открыть код метода в _новой вкладке");
		public static string CopyFullName => Pick("_Copy full name", "_Копировать полное имя");
		public static string CopyBlockIl => Pick("_Copy block IL", "_Копировать IL блока");
		public static string DoubleClickExplore(string name) => Pick("Double-click to explore " + name, "Двойной щелчок: исследовать " + name);
		public static string NoMethodBody => Pick("No method body", "У метода нет тела");
		public static string MoreLines(int count) => Pick($"… {count} more line{(count == 1 ? string.Empty : "s")} • open source for the complete method", $"… ещё строк: {count} • откройте исходный код для полного метода");
		public static string MethodTruncated => Pick("… method truncated • open source for the complete method", "… метод сокращён • откройте исходный код для полного метода");

		public static string MapLegendCallers => Pick("CALLERS", "ВЫЗЫВАЮЩИЕ");
		public static string MapLegendRoot => Pick("ROOT", "КОРЕНЬ");
		public static string MapLegendCallees => Pick("CALLEES", "ВЫЗЫВАЕМЫЕ");
		public static string MapLegendPossible => Pick("DASHED: POSSIBLE RUNTIME TARGET", "ПУНКТИР: ВОЗМОЖНАЯ RUNTIME-ЦЕЛЬ");
		public static string MapHint => Pick("Wheel: zoom  •  Drag: pan  •  Double-click: explore  •  Right-click: actions", "Колесо: масштаб  •  Перетаскивание: панорама  •  Двойной щелчок: исследовать  •  ПКМ: действия");
		public static string MethodsLinks(int methods, int links, bool truncated) => Pick($"{methods} methods  •  {links} links" + (truncated ? "  •  limit reached" : string.Empty), $"Методов: {methods}  •  Связей: {links}" + (truncated ? "  •  достигнут лимит" : string.Empty));
		public static string MethodInfo(int callers, int callees, int call_sites, int instructions, bool is_static) => Pick($"IL {instructions} instructions   •   {(is_static ? "static" : "instance")}\n{callers} callers   •   {callees} callees   •   {call_sites} call sites", $"IL-инструкций: {instructions}   •   {(is_static ? "статический" : "экземплярный")}\nВызывающих: {callers}   •   вызываемых: {callees}   •   мест вызова: {call_sites}");

		public static string NoSelection => Pick("Select a method in Assembly Explorer or place the caret on a method in C#/IL, then click Current selection.", "Выберите метод в обозревателе сборок или поставьте курсор на метод в C#/IL, затем нажмите «Текущий метод».");
		public static string NoSelectionStatus => Pick("No method is selected.", "Метод не выбран.");
		public static string BuildingCfg => Pick("Building control-flow graph …", "Построение CFG …");
		public static string Indexing(string module) => Pick("Indexing " + module + " …", "Индексирование " + module + " …");
		public static string IndexingWorkspace(int current, int total, string module) => Pick(
			$"Indexing loaded modules {current}/{total}: {module} …",
			$"Индексирование загруженных модулей {current}/{total}: {module} …");
		public static string IndexingWorkspaceParallel(int missing, int total, int workers) => Pick(
			$"Indexing {missing} of {total} loaded modules on {workers} workers …",
			$"Индексирование {missing} из {total} загруженных модулей в {workers} потоках …");
		public static string WorkspaceChanged => Pick("Loaded assemblies changed; updating the graph …", "Набор загруженных сборок изменён; граф обновляется …");
		public static string ModuleChanged => Pick("Module changed; rebuilding the graph …", "Модуль изменён; граф перестраивается …");
		public static string Decompiling => Pick("Decompiling graph blocks …", "Декомпиляция блоков графа …");
		public static string LoadingCode => Pick("Loading code …", "Загрузка кода …");
		public static string GraphError(string message) => Pick("Graph render error: " + message, "Ошибка отрисовки графа: " + message);
		public static string ModuleMissing => Pick("The selected method is not attached to a module.", "Выбранный метод не принадлежит модулю.");
		public static string CfgStatus(string signature, int blocks, int edges, int? limit) => Pick(
			$"CFG: {signature}   •   {blocks} basic blocks   •   {edges} edges" + (limit is int value ? $"   •   limited to {value} blocks" : string.Empty),
			$"CFG: {signature}   •   базовых блоков: {blocks}   •   рёбер: {edges}" + (limit is int value_ru ? $"   •   ограничено до {value_ru} блоков" : string.Empty));
		public static string IndexedGraphStatus(string prefix, int methods, int edges, int index_modules, int index_methods, int index_links, bool truncated) => Pick(
			$"{prefix}   •   {methods} methods   •   {edges} edges   •   workspace index {index_modules} modules / {index_methods} methods / {index_links} links" + (truncated ? "   •   node limit reached" : string.Empty),
			$"{prefix}   •   методов: {methods}   •   рёбер: {edges}   •   общий индекс: {index_modules} модулей / {index_methods} методов / {index_links} связей" + (truncated ? "   •   достигнут лимит узлов" : string.Empty));
		public static string DecompilerUnavailable(string language) => Pick(language + " decompiler is not available.", "Декомпилятор " + language + " недоступен.");
		public static string RenderFailure(string message) => Pick("Graph could not be rendered: " + message, "Не удалось отрисовать граф: " + message);
	}
}
