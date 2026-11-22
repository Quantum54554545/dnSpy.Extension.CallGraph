using System;
using System.Globalization;

namespace dnSpy.CallGraph {
		static class ui_text {
		static bool russian => string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);
		public static string pick(string english, string russian) => ui_text.russian ? russian : english;

		public static string call_graph => ui_text.pick("Call Graph", "Граф вызовов");
		public static string show_in_call_graph => ui_text.pick("Show in Call Graph", "Показать в графе вызовов");
		public static string previous_root => ui_text.pick("Previous graph root", "Предыдущий корень графа");
		public static string next_root => ui_text.pick("Next graph root", "Следующий корень графа");
		public static string current_selection => ui_text.pick("Current selection", "Текущий метод");
		public static string build_current_selection => ui_text.pick("Build the selected view for the current method", "Построить выбранный граф для текущего метода");
		public static string graph => ui_text.pick("Graph", "Граф");
		public static string calls => ui_text.pick("Calls", "Вызовы");
		public static string depth => ui_text.pick("Depth", "Глубина");
		public static string maximum_depth => ui_text.pick("Max", "Макс");
		public static string code => ui_text.pick("Code", "Код");
		public static string center_root => ui_text.pick("Center root", "Корень в центр");
		public static string center_root_tip => ui_text.pick("Center the current root block", "Переместить текущий корневой блок в центр");
		public static string search => ui_text.pick("Search", "Поиск");
		public static string search_method_tip => ui_text.pick("Find a visible method by name, type, or signature. Enter: next; Shift+Enter: previous.", "Найти видимый метод по имени, типу или сигнатуре. Enter: следующий; Shift+Enter: предыдущий.");
		public static string previous_match => ui_text.pick("Previous match", "Предыдущее совпадение");
		public static string next_match => ui_text.pick("Next match", "Следующее совпадение");
		public static string clear_search => ui_text.pick("Clear search", "Очистить поиск");
		public static string search_unavailable_in_cfg => ui_text.pick("Method search is available in Call blocks and Function map.", "Поиск методов доступен в «Блоках вызовов» и «Карте функций».");
		public static string search_results(int current, int total) => total == 0 ? ui_text.pick("No matches", "Нет совпадений") : $"{current} / {total}";
		public static string call_blocks => ui_text.pick("Call blocks", "Блоки вызовов");
		public static string function_map => ui_text.pick("Function map", "Карта функций");
		public static string callees => ui_text.pick("Callees", "Вызываемые");
		public static string callers => ui_text.pick("Callers", "Вызывающие");
		public static string both => ui_text.pick("Both", "Оба");
		public static string view_mode_tip => ui_text.pick("Call blocks: method code\nFunction map: fast overview\nCFG: IL basic blocks inside one method", "Блоки вызовов: код методов\nКарта функций: быстрый обзор\nCFG: базовые IL-блоки одного метода");
		public static string direction_tip => ui_text.pick("Callees: methods called by the root\nCallers: methods that call the root\nBoth: both directions", "Вызываемые: методы, которые вызывает корень\nВызывающие: методы, вызывающие корень\nОба: оба направления");
		public static string depth_tip => ui_text.pick("Call levels from 1 to 8; saved between sessions. The card limit is independent.", "Число уровней вызовов от 1 до 8; выбор сохраняется между запусками. Лимит карточек действует отдельно.");
		public static string language_tip => ui_text.pick("Decompiler used inside graph blocks", "Декомпилятор для кода внутри блоков");
		public static string loading_preview => ui_text.pick("Loading code preview …", "Загрузка кода …");
		public static string analyzing_reachable => ui_text.pick("Analyzing reachable calls …", "Анализ достижимых вызовов …");
		public static string reachable_graph_status(string root, int nodes, int links, bool limited) => ui_text.pick(
			$"{root}   •   {nodes} methods   •   {links} links   •   call-site context analysis" + (limited ? "   •   traversal limit reached" : ""),
			$"{root}   •   методов: {nodes}   •   связей: {links}   •   анализ аргументов вызовов" + (limited ? "   •   достигнут лимит обхода" : ""));
		public static string preview_il_fallback => ui_text.pick("Preview time limit reached; showing IL", "Превышено время предпросмотра; показан IL");
		public static string unresolved_calls(int count) => count == 0 ? string.Empty : ui_text.pick($"   •   unresolved calli sites: {count}", $"   •   не разрешены calli: {count}");

		public static string settings_heading => ui_text.pick("Call Graph blocks", "Блоки графа вызовов");
		public static string lines_title => ui_text.pick("Lines shown in each block", "Строк в каждом блоке");
		public static string lines_description => ui_text.pick("Auto keeps small methods complete and previews up to 20 code rows in large cards, including wrapped text. Open full source in dnSpy for more. A selected count overrides the preview size; it never filters graph relations.", "Авто оставляет маленькие методы целиком, а большие сокращает до 20 строк кода с учётом переносов. Полный код открывается в обычной вкладке dnSpy. Выбранное число меняет размер предпросмотра, а не связи графа.");
		public static string auto_lines => ui_text.pick("Auto (20)", "Авто (20)");
		public static string wrap_lines => ui_text.pick("Wrap long code lines", "Переносить длинные строки кода");
		public static string graph_filters => ui_text.pick("Graph filters", "Фильтры графа");
		public static string graph_filters_description => ui_text.pick("Filters apply to Call blocks and Function map. The root method is always kept visible.", "Фильтры применяются к блокам вызовов и карте функций. Корневой метод всегда остаётся видимым.");
		public static string hide_system_methods => ui_text.pick("Hide System.* and Microsoft.* methods", "Скрывать методы System.* и Microsoft.*");
		public static string hide_accessors => ui_text.pick("Hide property and event accessors", "Скрывать аксессоры свойств и событий");
		public static string hide_constructors => ui_text.pick("Hide constructors", "Скрывать конструкторы");
		public static string only_current_namespace => ui_text.pick("Only the current namespace", "Только текущее пространство имён");
		public static string only_current_type => ui_text.pick("Only the current type", "Только текущий тип");
		public static string only_current_assembly => ui_text.pick("Limit initial traversal to the current assembly", "Ограничить начальный обход текущей сборкой");
		public static string only_current_assembly_description => ui_text.pick("External calls remain visible as leaves; only methods from the root assembly are expanded. Explore from an external method to continue there.", "Внешние вызовы остаются видны как листья; раскрываются только методы текущей сборки. Для продолжения выберите внешний метод и «Исследовать отсюда».");
		public static string require_method_body => ui_text.pick("Only methods with an IL body", "Только методы с IL-телом");
		public static string assembly_explorer => ui_text.pick("Assembly Explorer", "Обозреватель сборок");
		public static string clean_explorer => ui_text.pick("On launch, clear restored assemblies and keep only command-line files", "При запуске очищать восстановленные сборки и оставлять только файлы из командной строки");
		public static string clean_explorer_description => ui_text.pick("Only on startup, dnSpy closes restored document tabs and removes assemblies from the previous session. Files subsequently sent to the running instance do not clear your work.", "Только при запуске dnSpy закроет восстановленные вкладки и удалит сборки из прошлого сеанса. Последующая передача файлов в запущенный dnSpy не очищает текущую работу.");

		public static string explore_here => ui_text.pick("_Explore from here", "_Исследовать отсюда");
		public static string show_method_in_cfg => ui_text.pick("Show method in _CFG", "Показать метод в _CFG");
		public static string open_source => ui_text.pick("Open source in _new tab", "Открыть код в _новой вкладке");
		public static string open_method_source => ui_text.pick("Open method source in _new tab", "Открыть код метода в _новой вкладке");
		public static string copy_full_name => ui_text.pick("_Copy full name", "_Копировать полное имя");
		public static string copy_block_il => ui_text.pick("_Copy block IL", "_Копировать IL блока");
		public static string double_click_explore(string name) => ui_text.pick("Double-click to explore " + name, "Двойной щелчок: исследовать " + name);
		public static string no_method_body => ui_text.pick("No inspectable IL body", "Нет доступного IL-тела метода");
		public static string more_lines(int count) => ui_text.pick($"… {count} more line{(count == 1 ? string.Empty : "s")} • open source for the complete method", $"… ещё строк: {count} • откройте исходный код для полного метода");
		public static string preview_truncated => ui_text.pick("… code preview shortened", "… предпросмотр сокращён");
		public static string open_full_source => ui_text.pick("Open full source", "Открыть полный код");
		public static string method_truncated => ui_text.pick("… method truncated • open source for the complete method", "… метод сокращён • откройте исходный код для полного метода");

		public static string map_legend_callers => ui_text.pick("CALLERS", "ВЫЗЫВАЮЩИЕ");
		public static string map_legend_root => ui_text.pick("ROOT", "КОРЕНЬ");
		public static string map_legend_callees => ui_text.pick("CALLEES", "ВЫЗЫВАЕМЫЕ");
		public static string map_legend_possible => ui_text.pick("DASHED: POSSIBLE RUNTIME TARGET", "ПУНКТИР: ВОЗМОЖНАЯ RUNTIME-ЦЕЛЬ");
		public static string map_hint => ui_text.pick("Drag node: move  •  Drag background: pan  •  Hover: code  •  Double-click: explore", "Узел: переместить  •  Фон: панорама  •  Наведение: код  •  Двойной щелчок: исследовать");
		public static string methods_links(int methods, int links, bool truncated) => ui_text.pick($"{methods} methods  •  {links} links" + (truncated ? "  •  traversal limited" : string.Empty), $"Методов: {methods}  •  Связей: {links}" + (truncated ? "  •  обход ограничен" : string.Empty));
		public static string method_info(int callers, int callees, int call_sites, int instructions, bool is_static) => ui_text.pick($"IL {instructions} instructions   •   {(is_static ? "static" : "instance")}\n{callers} callers   •   {callees} callees   •   {call_sites} call sites", $"IL-инструкций: {instructions}   •   {(is_static ? "статический" : "экземплярный")}\nВызывающих: {callers}   •   вызываемых: {callees}   •   мест вызова: {call_sites}");

		public static string no_selection => ui_text.pick("Select a method in Assembly Explorer or place the caret on a method in C#/IL, then click Current selection.", "Выберите метод в обозревателе сборок или поставьте курсор на метод в C#/IL, затем нажмите «Текущий метод».");
		public static string no_selection_status => ui_text.pick("No method is selected.", "Метод не выбран.");
		public static string building_cfg => ui_text.pick("Building control-flow graph …", "Построение CFG …");
		public static string indexing(string module) => ui_text.pick("Indexing " + module + " …", "Индексирование " + module + " …");
		public static string indexing_workspace(int current, int total, string module) => ui_text.pick(
			$"Indexing loaded modules {current}/{total}: {module} …",
			$"Индексирование загруженных модулей {current}/{total}: {module} …");
		public static string indexing_workspace_parallel(int missing, int total, int workers) => ui_text.pick(
			$"Indexing {missing} of {total} loaded modules on {workers} workers …",
			$"Индексирование {missing} из {total} загруженных модулей в {workers} потоках …");
		public static string workspace_changed => ui_text.pick("Loaded assemblies changed; updating the graph …", "Набор загруженных сборок изменён; граф обновляется …");
		public static string module_changed => ui_text.pick("Module changed; rebuilding the graph …", "Модуль изменён; граф перестраивается …");
		public static string decompiling => ui_text.pick("Decompiling graph blocks …", "Декомпиляция блоков графа …");
		public static string loading_code => ui_text.pick("Loading code …", "Загрузка кода …");
		public static string graph_error(string message) => ui_text.pick("Graph render error: " + message, "Ошибка отрисовки графа: " + message);
		public static string module_missing => ui_text.pick("The selected method is not attached to a module.", "Выбранный метод не принадлежит модулю.");
		public static string cfg_status(string signature, int blocks, int edges, int? limit) => ui_text.pick(
			$"CFG: {signature}   •   {blocks} basic blocks   •   {edges} edges" + (limit is int value ? $"   •   limited to {value} blocks" : string.Empty),
			$"CFG: {signature}   •   базовых блоков: {blocks}   •   рёбер: {edges}" + (limit is int value_ru ? $"   •   ограничено до {value_ru} блоков" : string.Empty));
		public static string indexed_graph_status(string prefix, int methods, int edges, int index_modules, int index_methods, int index_links, bool truncated) => ui_text.pick(
			$"{prefix}   •   {methods} methods   •   {edges} edges   •   workspace index {index_modules} modules / {index_methods} methods / {index_links} links" + (truncated ? "   •   traversal limited; see analysis notes" : string.Empty),
			$"{prefix}   •   методов: {methods}   •   рёбер: {edges}   •   общий индекс: {index_modules} модулей / {index_methods} методов / {index_links} связей" + (truncated ? "   •   обход ограничен; см. замечания анализа" : string.Empty));
		public static string decompiler_unavailable(string language) => ui_text.pick(language + " decompiler is not available.", "Декомпилятор " + language + " недоступен.");
		public static string render_failure(string message) => ui_text.pick("Graph could not be rendered: " + message, "Не удалось отрисовать граф: " + message);
	}
}
