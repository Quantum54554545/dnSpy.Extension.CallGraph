using System.Collections.Generic;
using dnSpy.Contracts.Extension;

namespace dnSpy.CallGraph {
	[ExportExtension]
	sealed class the_extension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = ui_text.pick("Method call blocks, optimized function map and IL control-flow graph", "Блоки вызовов методов, оптимизированная карта функций и граф потока управления IL"),
			Copyright = "Call Graph extension for dnSpyEx",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) { }
	}
}
