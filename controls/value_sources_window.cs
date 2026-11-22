using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using dnSpy.Contracts.Themes;
using dnSpy.Contracts.Controls;

namespace dnSpy.CallGraph.Controls {
	sealed class value_sources_window : MetroWindow {
		readonly MethodDef method;
		readonly workspace_analysis workspace;
		readonly value_sources sources;
		readonly call_graph_surface surface;
		readonly TextBlock status;
		readonly ComboBox sites;
		readonly Dictionary<value_origin, value_dependency[]> parents = new();
		readonly CancellationTokenSource lifetime = new();
		CancellationTokenSource? operation;
		value_origin? root;
		bool closed;
		sealed record site_choice(int position, string title);
		public value_sources_window(MethodDef method, workspace_analysis workspace, workspace_search index, IThemeService theme, Action<MethodDef> navigate) {
			this.method = method; this.workspace = workspace; this.sources = new value_sources(workspace, index);
			Title = ui_text.pick("Value sources — ", "Источники значений — ") + call_graph_index.display_name(method); Width = 1100; Height = 720; MinWidth = 680; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
			SetResourceReference(StyleProperty, "DialogWindowStyle"); ShowMaximizeButton = true; ShowMinimizeButton = true;
			var panel = new DockPanel(); var toolbar = new DockPanel { Margin = new Thickness(7) }; DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
			var center = new Button { Content = ui_text.center_root, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0) }; DockPanel.SetDock(center, Dock.Right); toolbar.Children.Add(center);
			var cancel = new Button { Content = ui_text.pick("Cancel", "Отмена"), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0) }; DockPanel.SetDock(cancel, Dock.Right); toolbar.Children.Add(cancel);
			this.sites = new ComboBox { DisplayMemberPath = "Title", MinWidth = 300 }; toolbar.Children.Add(this.sites);
			this.status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 5, 8, 7) }; this.status.SetResourceReference(TextBlock.ForegroundProperty, "SystemColorsWindowText"); DockPanel.SetDock(this.status, Dock.Bottom); panel.Children.Add(this.status);
			this.surface = new call_graph_surface(theme); this.surface.configure_sources(); panel.Children.Add(this.surface); Content = panel;
			center.Click += (_, __) => this.surface.center_root(); cancel.Click += (_, __) => this.operation?.Cancel();
			this.surface.research_requested += async (_, event_args) => { if (event_args.reference is value_origin origin) { if (event_args.action == research_action.sources) this.root = origin; if (event_args.action is research_action.expand or research_action.sources) await this.expand(origin); } };
			this.surface.edge_requested += (_, event_args) => { if (event_args.reference is graph_link_presentation link) this.status.Text = link.tool_tip; };
			this.surface.navigate_requested += (_, event_args) => { if (event_args.reference is value_origin origin) navigate(origin.method); };
			this.surface.root_requested += async (_, event_args) => { if (event_args.reference is value_origin origin) await this.expand(origin); else if (event_args.reference is MethodDef target) navigate(target); };
			this.sites.SelectionChanged += async (_, __) => { if (this.sites.SelectedItem is site_choice choice) { this.parents.Clear(); this.root = new value_origin(method, choice.position); await this.expand(this.root); } };
			var choices = method.HasBody ? method.Body.Instructions.Select((instruction, position) => (instruction: instruction, position: position)).Where(item => item.instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Calli or Code.Newobj or Code.Stfld or Code.Stsfld or Code.Ret).Select(item => new site_choice(item.position, item.instruction.ToString())).ToArray() : Array.Empty<site_choice>();
			this.sites.ItemsSource = choices;
			Loaded += (_, __) => { if (choices.Length != 0) this.sites.SelectedIndex = Array.FindIndex(choices, choice => method.Body.Instructions[choice.position].OpCode.Code == Code.Calli) is int preferred && preferred >= 0 ? preferred : 0; else this.status.Text = ui_text.no_method_body; };
			Closed += (_, __) => { this.closed = true; this.lifetime.Cancel(); this.operation?.Cancel(); this.surface.clear(); this.lifetime.Dispose(); this.operation?.Dispose(); };
		}
		async Task expand(value_origin origin) {
			this.operation?.Cancel(); this.operation?.Dispose(); this.operation = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token); var token = this.operation.Token; var expected_root = this.root;
			this.status.Text = ui_text.pick("Computing IL value dependencies…", "Построение зависимостей значений по IL…");
			try {
				await this.workspace.gate.WaitAsync(token); value_dependency[] values;
				try { values = await Task.Run(() => this.sources.parents(origin, token), token); } finally { this.workspace.gate.Release(); }
				if (this.closed || token.IsCancellationRequested || expected_root != this.root) return;
				this.parents[origin] = values; this.render();
			}
			catch (OperationCanceledException) { if (!this.closed && this.operation?.Token == token) this.status.Text = ui_text.pick("Cancelled; previous graph retained", "Отменено; предыдущий граф сохранён"); }
			catch (Exception error) { if (!this.closed && this.operation?.Token == token) this.status.Text = error.Message; }
		}
		void render() {
			if (this.root is null) return;
			var layers = new Dictionary<value_origin, int> { [this.root] = 0 }; var pending = new Queue<value_origin>(); pending.Enqueue(this.root); int hidden = 0;
			while (pending.Count != 0) {
				var target = pending.Dequeue();
				foreach (var dependency in this.parents.GetValueOrDefault(target) ?? Array.Empty<value_dependency>()) {
					if (layers.ContainsKey(dependency.source)) continue;
					if (layers.Count >= 120) { hidden++; continue; }
					layers[dependency.source] = layers[target] - 1; pending.Enqueue(dependency.source);
				}
			}
			var snippets = new Dictionary<object, highlighted_snippet>(); var nodes = new List<graph_node_presentation>();
			foreach (var pair in layers) {
				var origin = pair.Key;
				string text = origin.position < 0 ? ui_text.pick("Unknown / external value", "Неизвестное / внешнее значение") : origin.method.Body.Instructions[origin.position].ToString();
				string title = origin.position == -2 ? ui_text.pick("Exception value", "Значение исключения") : origin.position < 0 ? "?" : $"IL_{origin.method.Body.Instructions[origin.position].Offset:X4}: {origin.method.Body.Instructions[origin.position].OpCode.Name}";
				string continuation = origin.position >= 0 && !this.parents.ContainsKey(origin) ? ui_text.pick("Expand value sources", "Раскрыть источники значения") : "";
				nodes.Add(new graph_node_presentation(origin, pair.Value, origin == this.root, origin.position < 0, title, call_graph_index.display_name(origin.method), text, text, continuation)); snippets[origin] = origin.position < 0 ? highlighted_snippet.plain(text) : control_flow_block.build_snippet(new[] { origin.method.Body.Instructions[origin.position] });
			}
			var links = this.parents.SelectMany(pair => pair.Value.Where(dependency => layers.ContainsKey(pair.Key) && layers.ContainsKey(dependency.source)).Select(dependency => new graph_link_presentation(dependency.source, pair.Key, 1, dependency.reason, dependency.label, graph_link_kind.value_dependency))).ToArray();
			this.surface.render(new graph_presentation(nodes, links), snippets);
			this.status.Text = ui_text.pick("Conservative data dependencies, not execution order. Click + to follow locals, writes, returns and arguments. Field aliasing, indirect memory and external callers may be unknown.", "Консервативные зависимости данных, не порядок исполнения. «+» раскрывает локальные значения, записи, возвраты и аргументы. Алиасы полей, косвенная память и внешние вызывающие могут быть неизвестны.") + (hidden == 0 ? "" : ui_text.pick($" Hidden at view limit: {hidden}; select a different starting site.", $" Скрыто размером фрагмента: {hidden}; выбери другую исходную инструкцию."));
		}
	}
}
