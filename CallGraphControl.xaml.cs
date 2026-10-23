using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;
using dnSpy.CallGraph.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;
using dnSpy.Contracts.Themes;

namespace dnSpy.CallGraph {
	partial class CallGraphControl : UserControl {
		enum GraphViewMode { CallBlocks, FunctionMap, ControlFlow }

		readonly IDocumentTabService document_tab_service;
		readonly IDecompilerService decompiler_service;
		readonly CallGraphSettings settings;
		readonly IThemeService theme_service;
		readonly CallGraphIndexCache index_cache;
		readonly Dictionary<SnippetCacheKey, HighlightedSnippet> snippet_cache = new Dictionary<SnippetCacheKey, HighlightedSnippet>();
		readonly List<MethodDef> history = new List<MethodDef>();
		readonly List<MethodDef> visible_methods = new List<MethodDef>();
		readonly List<MethodDef> search_matches = new List<MethodDef>();
		readonly DispatcherTimer document_change_timer;
		CancellationTokenSource? operation_cancellation;
		CancellationTokenSource? preview_cancellation;
		MethodDef? current_root;
		CallGraphIndex? current_index;
		bool current_index_covers_all_loaded_modules;
		int history_position = -1;
		bool suppress_configuration_changed;
		GraphViewMode previous_view_mode = GraphViewMode.CallBlocks;
		int call_block_limit = 40;
		int function_map_limit = 500;
		int cfg_block_limit = 250;
		int search_position = -1;
		bool disposed;
		bool visible_snippets_enabled;
		bool visible_snippets_running;
		bool visible_snippets_requested;
		bool analyzing_graph;
		bool workspace_update_pending;
		HashSet<ModuleDef>? graph_modules;
		int current_preview_lines = 80;

		readonly struct SnippetCacheKey : IEquatable<SnippetCacheKey> {
			public readonly MethodDef Definition;
			public readonly Guid Language;
			public readonly int MaximumLines;

			public SnippetCacheKey(MethodDef definition, Guid language, int maximum_lines) {
				Definition = definition;
				Language = language;
				MaximumLines = maximum_lines;
			}

			public bool Equals(SnippetCacheKey other) => ReferenceEquals(Definition, other.Definition) &&
				Language == other.Language && MaximumLines == other.MaximumLines;
			public override bool Equals(object? obj) => obj is SnippetCacheKey other && Equals(other);
			public override int GetHashCode() => ((Definition.GetHashCode() * 397) ^ Language.GetHashCode()) * 397 ^ MaximumLines;
		}

		GraphViewMode CurrentViewMode => (GraphViewMode)Math.Max(0, ViewModeComboBox.SelectedIndex);

		internal CallGraphControl(IDocumentTabService document_tab_service, IDecompilerService decompiler_service,
			CallGraphSettings settings, IThemeService theme_service, CallGraphIndexCache index_cache) {
			this.document_tab_service = document_tab_service;
			this.decompiler_service = decompiler_service;
			this.settings = settings;
			this.theme_service = theme_service;
			this.index_cache = index_cache;
			InitializeComponent();
			ApplyLocalizedText();
			GraphSurface.SetThemeService(theme_service);
			FunctionMapSurface.SetThemeService(theme_service);
			GraphSurface.Configure(settings);
			InitializeToolbar();
			document_change_timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) {
				Interval = TimeSpan.FromMilliseconds(350),
			};
			document_change_timer.Tick += DocumentChangeTimer_Tick;
			settings.Changed += Settings_Changed;
			theme_service.ThemeChanged += ThemeService_ThemeChanged;
			document_tab_service.DocumentModified += DocumentTabService_DocumentModified;
			document_tab_service.DocumentCollectionChanged += DocumentTabService_DocumentCollectionChanged;
			ShowNoSelectionMessage();
		}

		void ApplyLocalizedText() {
			BackButton.ToolTip = UiText.PreviousRoot;
			ForwardButton.ToolTip = UiText.NextRoot;
			LoadSelectionButton.Content = UiText.CurrentSelection;
			LoadSelectionButton.ToolTip = UiText.BuildCurrentSelection;
			GraphLabel.Text = UiText.Graph;
			CallsLabel.Text = UiText.Calls;
			DepthLabel.Text = UiText.Depth;
			LimitLabel.Text = UiText.Limit;
			CodeLabel.Text = UiText.Code;
			CenterButton.Content = UiText.CenterRoot;
			CenterButton.ToolTip = UiText.CenterRootTip;
			ViewModeComboBox.ToolTip = UiText.ViewModeTip;
			DirectionComboBox.ToolTip = UiText.DirectionTip;
			DepthComboBox.ToolTip = UiText.DepthTip;
			NodeLimitComboBox.ToolTip = UiText.NodeLimitTip;
			LanguageComboBox.ToolTip = UiText.LanguageTip;
			SearchLabel.Text = UiText.Search;
			SearchTextBox.ToolTip = UiText.SearchMethodTip;
			SearchPreviousButton.ToolTip = UiText.PreviousMatch;
			SearchNextButton.ToolTip = UiText.NextMatch;
			ClearSearchButton.ToolTip = UiText.ClearSearch;
		}

		void Settings_Changed(object? sender, EventArgs e) {
			GraphSurface.Configure(settings);
			QueueRender();
		}

		void ThemeService_ThemeChanged(object? sender, ThemeChangedEventArgs e) => QueueRender();

		void DocumentTabService_DocumentModified(object? sender, DocumentModifiedEventArgs e) {
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.BeginInvoke(new Action(() => DocumentTabService_DocumentModified(sender, e)));
				return;
			}
			if (disposed)
				return;
			var modules = e.Documents.GetModules<ModuleDef>().Distinct().ToArray();
			if (modules.Length == 0)
				return;
			index_cache.Invalidate(modules);
			var modified = new HashSet<ModuleDef>(modules);
			lock (snippet_cache) {
				foreach (var key in snippet_cache.Keys.Where(key => key.Definition.Module is ModuleDef module && modified.Contains(module)).ToArray())
					snippet_cache.Remove(key);
			}
			bool affects_current_index = current_index?.Modules.Any(modified.Contains) == true ||
				(current_root?.Module is ModuleDef root_module && modified.Contains(root_module));
			if (!affects_current_index)
				return;
			operation_cancellation?.Cancel();
			current_index = null;
			current_index_covers_all_loaded_modules = false;
			SetBusy(true, UiText.ModuleChanged);
			document_change_timer.Stop();
			document_change_timer.Start();
		}

		void DocumentTabService_DocumentCollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.BeginInvoke(new Action(() => DocumentTabService_DocumentCollectionChanged(sender, e)));
				return;
			}
			if (disposed)
				return;
			var loaded_modules = new HashSet<ModuleDef>(GetLoadedModules());
			index_cache.RemoveMissing(loaded_modules);
			lock (snippet_cache) {
				foreach (var key in snippet_cache.Keys.Where(key => key.Definition.Module is not ModuleDef module || !loaded_modules.Contains(module)).ToArray())
					snippet_cache.Remove(key);
			}
			if ((analyzing_graph || visible_snippets_running) && graph_modules is not null &&
				graph_modules.IsSubsetOf(loaded_modules) && current_root?.Module is ModuleDef current_module && loaded_modules.Contains(current_module)) {
				workspace_update_pending = true;
				return;
			}
			current_index = null;
			current_index_covers_all_loaded_modules = false;
			operation_cancellation?.Cancel();
			if (current_root?.Module is not ModuleDef root_module || !loaded_modules.Contains(root_module)) {
				current_root = null;
				history.RemoveAll(method => method.Module is ModuleDef module && !loaded_modules.Contains(module));
				history_position = history.Count - 1;
				UpdateHistoryButtons();
				ShowNoSelectionMessage();
				return;
			}
			SetBusy(true, UiText.WorkspaceChanged);
			document_change_timer.Stop();
			document_change_timer.Start();
		}

		void DocumentChangeTimer_Tick(object? sender, EventArgs e) {
			document_change_timer.Stop();
			QueueRender();
		}

		void InitializeToolbar() {
			ViewModeComboBox.ItemsSource = new[] { UiText.CallBlocks, UiText.FunctionMap, "CFG" };
			ViewModeComboBox.SelectedIndex = 0;
			DirectionComboBox.ItemsSource = new[] { UiText.Callees, UiText.Callers, UiText.Both };
			DirectionComboBox.SelectedIndex = 0;
			DepthComboBox.ItemsSource = Enumerable.Range(1, 8).Cast<object>().Concat(new object[] { UiText.MaximumDepth }).ToArray();
			DepthComboBox.SelectedItem = 2;
			LanguageComboBox.ItemsSource = new[] { "C#", "IL" };
			LanguageComboBox.SelectedIndex = 0;
			SetNodeLimitChoices(GraphViewMode.CallBlocks);

			LoadSelectionButton.Click += (_, __) => LoadCurrentSelection();
			BackButton.Click += (_, __) => NavigateHistory(-1);
			ForwardButton.Click += (_, __) => NavigateHistory(+1);
			CenterButton.Click += (_, __) => CenterCurrentView();
			ViewModeComboBox.SelectionChanged += ViewModeChanged;
			DirectionComboBox.SelectionChanged += ConfigurationChanged;
			DepthComboBox.SelectionChanged += ConfigurationChanged;
			NodeLimitComboBox.SelectionChanged += ConfigurationChanged;
			LanguageComboBox.SelectionChanged += ConfigurationChanged;
			SearchTextBox.TextChanged += (_, __) => UpdateSearchResults();
			SearchTextBox.KeyDown += SearchTextBox_KeyDown;
			SearchPreviousButton.Click += (_, __) => MoveSearch(-1);
			SearchNextButton.Click += (_, __) => MoveSearch(+1);
			ClearSearchButton.Click += (_, __) => SearchTextBox.Clear();

			GraphSurface.NavigateRequested += (_, e) => document_tab_service.FollowReference(e.Reference, newTab: true);
			GraphSurface.RootRequested += (_, e) => { if (ResolveMethod(e.Reference) is MethodDef method) ShowMethod(method); };
			GraphSurface.SelectionChanged += (_, e) => StatusText.Text = FormatReferenceStatus(e.Reference);
			GraphSurface.VisibleMethodsChanged += (_, __) => RequestVisibleSnippets();
			FunctionMapSurface.NavigateRequested += (_, e) => document_tab_service.FollowReference(e.Reference, newTab: true);
			FunctionMapSurface.RootRequested += (_, e) => { if (e.Reference is MethodDef method) ShowMethod(method); };
			FunctionMapSurface.SelectionChanged += (_, e) => StatusText.Text = FormatReferenceStatus(e.Reference);
			FunctionMapSurface.CodePreviewRequested += (_, e) => RequestFunctionMapPreview(e.Reference as MethodDef);
			UpdateModeControls();
		}

		void SearchTextBox_KeyDown(object sender, KeyEventArgs e) {
			if (e.Key != Key.Enter)
				return;
			MoveSearch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
			e.Handled = true;
		}

		void SetVisibleMethods(IEnumerable<MethodDef> methods) {
			visible_methods.Clear();
			visible_methods.AddRange(methods.Distinct());
			UpdateSearchResults();
		}

		void UpdateSearchResults() {
			search_matches.Clear();
			search_position = -1;
			string query = SearchTextBox.Text.Trim();
			if (query.Length != 0 && CurrentViewMode != GraphViewMode.ControlFlow) {
				search_matches.AddRange(visible_methods.Where(method =>
					CallGraphIndex.DisplayName(method).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
					CallGraphIndex.Signature(method).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0));
				if (search_matches.Count != 0)
					search_position = 0;
			}
			bool has_query = query.Length != 0;
			SearchPreviousButton.IsEnabled = SearchNextButton.IsEnabled = search_matches.Count != 0;
			ClearSearchButton.IsEnabled = has_query;
			SearchResultText.Text = has_query ? UiText.SearchResults(search_position + 1, search_matches.Count) : string.Empty;
			if (search_position >= 0)
				FocusSearchResult();
		}

		void MoveSearch(int delta) {
			if (search_matches.Count == 0)
				return;
			search_position = (search_position + delta + search_matches.Count) % search_matches.Count;
			SearchResultText.Text = UiText.SearchResults(search_position + 1, search_matches.Count);
			FocusSearchResult();
		}

		void FocusSearchResult() {
			if (search_position < 0 || search_position >= search_matches.Count)
				return;
			var method = search_matches[search_position];
			if (CurrentViewMode == GraphViewMode.FunctionMap)
				FunctionMapSurface.FocusMethod(method);
			else
				GraphSurface.FocusReference(method);
		}

		void ViewModeChanged(object sender, SelectionChangedEventArgs e) {
			if (suppress_configuration_changed)
				return;
			RememberNodeLimit(previous_view_mode);
			previous_view_mode = CurrentViewMode;
			UpdateModeControls();
			QueueRender();
		}

		void ConfigurationChanged(object sender, SelectionChangedEventArgs e) {
			if (suppress_configuration_changed)
				return;
			RememberNodeLimit(CurrentViewMode);
			QueueRender();
		}

		void UpdateModeControls() {
			var mode = CurrentViewMode;
			suppress_configuration_changed = true;
			SetNodeLimitChoices(mode);
			suppress_configuration_changed = false;
			bool is_cfg = mode == GraphViewMode.ControlFlow;
			DirectionComboBox.IsEnabled = !is_cfg;
			DepthComboBox.IsEnabled = !is_cfg;
			NodeLimitComboBox.IsEnabled = true;
			LanguageComboBox.IsEnabled = !is_cfg;
			SearchTextBox.IsEnabled = !is_cfg;
			if (is_cfg)
				SearchTextBox.ToolTip = UiText.SearchUnavailableInCfg;
			else
				SearchTextBox.ToolTip = UiText.SearchMethodTip;
			GraphSurface.Visibility = mode == GraphViewMode.FunctionMap ? Visibility.Collapsed : Visibility.Visible;
			FunctionMapSurface.Visibility = mode == GraphViewMode.FunctionMap ? Visibility.Visible : Visibility.Collapsed;
			UpdateSearchResults();
		}

		void SetNodeLimitChoices(GraphViewMode mode) {
			if (mode == GraphViewMode.FunctionMap) {
				NodeLimitComboBox.ItemsSource = new[] { 120, 250, 500, 1000, 2000 };
				NodeLimitComboBox.SelectedItem = function_map_limit;
			}
			else if (mode == GraphViewMode.ControlFlow) {
				NodeLimitComboBox.ItemsSource = new[] { 100, 250, 500 };
				NodeLimitComboBox.SelectedItem = cfg_block_limit;
			}
			else {
				NodeLimitComboBox.ItemsSource = new[] { 20, 40, 80, 120, 250, 500 };
				NodeLimitComboBox.SelectedItem = call_block_limit;
			}
		}

		void RememberNodeLimit(GraphViewMode mode) {
			if (NodeLimitComboBox.SelectedItem is not int value)
				return;
			if (mode == GraphViewMode.FunctionMap)
				function_map_limit = value;
			else if (mode == GraphViewMode.CallBlocks)
				call_block_limit = value;
			else
				cfg_block_limit = value;
		}

		void CenterCurrentView() {
			if (CurrentViewMode == GraphViewMode.FunctionMap)
				FunctionMapSurface.CenterRoot();
			else
				GraphSurface.CenterRoot();
		}

		static string FormatReferenceStatus(object reference) {
			if (reference is MethodDef method)
				return CallGraphIndex.Signature(method) + $"   token 0x{method.MDToken.Raw:X8}";
			if (reference is ControlFlowBlock block)
				return $"{CallGraphIndex.Signature(block.Method)}   •   {block.Title}–IL_{block.EndOffset:X4}   •   {block.Description}";
			return reference.ToString() ?? string.Empty;
		}

		public void FocusPrimaryControl() {
			if (CurrentViewMode == GraphViewMode.FunctionMap)
				FunctionMapSurface.Focus();
			else
				GraphSurface.Focus();
		}

		public void LoadCurrentSelection() {
			var method = GetCurrentMethod(document_tab_service);
			if (method is null) {
				ShowNoSelectionMessage();
				return;
			}
			ShowMethod(method);
		}

		internal void ShowNoSelectionMessage() {
			analyzing_graph = false; workspace_update_pending = false; graph_modules = null;
			GraphSurface.Clear(UiText.NoSelection);
			FunctionMapSurface.Clear();
			StatusText.Text = UiText.NoSelectionStatus;
			SetVisibleMethods(Array.Empty<MethodDef>());
		}

		public void ShowMethod(MethodDef method) {
			if (method.Module is null) {
				StatusText.Text = UiText.ModuleMissing;
				return;
			}
			current_root = method;
			if (current_index is not null && !current_index.Contains(method)) {
				current_index = null;
				current_index_covers_all_loaded_modules = false;
			}
			AddHistory(method);
			QueueRender();
		}

		void QueueRender() {
			if (disposed || current_root is null)
				return;
			operation_cancellation?.Cancel();
			visible_snippets_enabled = false;
			preview_cancellation?.Cancel();
			operation_cancellation?.Dispose();
			operation_cancellation = new CancellationTokenSource();
			_ = RenderCurrentAsync(operation_cancellation.Token);
		}

		async Task RenderCurrentAsync(CancellationToken cancellation_token) {
			var root = current_root;
			if (root?.Module is null)
				return;
			var mode = CurrentViewMode;
			analyzing_graph = true;
			try {
				if (mode == GraphViewMode.ControlFlow) {
					SetVisibleMethods(Array.Empty<MethodDef>());
					SetBusy(true, UiText.BuildingCfg);
					int block_limit = NodeLimitComboBox.SelectedItem is int selected_limit ? selected_limit : cfg_block_limit;
					var cfg = await Task.Run(() => ControlFlowGraph.Build(root, block_limit, cancellation_token), cancellation_token);
					cancellation_token.ThrowIfCancellationRequested();
					GraphSurface.Render(cfg, settings.EffectiveMaximumLines);
					if (cfg.Blocks.Count == 0) { StatusText.Text = UiText.NoMethodBody; return; }
					StatusText.Text = UiText.CfgStatus(CallGraphIndex.Signature(root), cfg.Blocks.Count, cfg.Edges.Count,
						cfg.IsTruncated ? block_limit : null);
					return;
				}

				var direction = (CallGraphDirection)Math.Max(0, DirectionComboBox.SelectedIndex);
				int depth = DepthComboBox.SelectedItem is int depth_value ? depth_value : int.MaxValue;
				int node_limit = NodeLimitComboBox.SelectedItem is int node_limit_value ? node_limit_value : 40;
				var filters = settings.CreateFilterOptions();
				var modules = GetLoadedModules();
				if (!modules.Contains(root.Module))
					modules.Insert(0, root.Module);
				graph_modules = new HashSet<ModuleDef>(modules);
				var index = current_index;
				CallGraphSlice slice;
				if (direction == CallGraphDirection.Callees) {
					SetBusy(true, UiText.AnalyzingReachable);
					var progress = new Progress<CallGraphSlice>(preview => {
						if (disposed || cancellation_token.IsCancellationRequested || !analyzing_graph || !ReferenceEquals(root, current_root) || mode != CurrentViewMode) return;
						if (mode == GraphViewMode.FunctionMap) FunctionMapSurface.Render(preview);
						else GraphSurface.Render(preview, preview.Nodes.ToDictionary(node => node.Method, _ => HighlightedSnippet.Plain(UiText.AnalyzingReachable)));
					});
					var reachable = await Task.Run(() => CallGraphIndex.BuildReachableProgressive(root, modules, depth, node_limit, filters, progress, cancellation_token), cancellation_token);
					cancellation_token.ThrowIfCancellationRequested();
					index = reachable.Index; slice = reachable.Slice;
					current_index = index;
					current_index_covers_all_loaded_modules = false;
				}
				else {
					if (index is null || index.IsReachableIndex || !current_index_covers_all_loaded_modules || !index.ContainsModule(root.Module)) {
						var module_indexes = await GetOrBuildModuleIndexesAsync(modules, cancellation_token);
						if (!index_cache.TryGetCombined(module_indexes, out index)) {
							index = await Task.Run(() => CallGraphIndex.Combine(module_indexes), cancellation_token);
							index_cache.StoreCombined(module_indexes, index);
						}
						cancellation_token.ThrowIfCancellationRequested();
						current_index = index;
						current_index_covers_all_loaded_modules = true;
					}
					if (index is null) return;
					slice = await Task.Run(() => index.Select(root, direction, depth, node_limit, filters, cancellation_token), cancellation_token);
				}
				cancellation_token.ThrowIfCancellationRequested();
				StatusText.ToolTip = AnalysisDetails.StatusToolTip(slice);
				if (mode == GraphViewMode.FunctionMap) {
					FunctionMapSurface.Render(slice, index);
					SetVisibleMethods(slice.Nodes.Select(node => node.Method));
					StatusText.Text = FormatGraphStatus(UiText.FunctionMap + ": " + CallGraphIndex.DisplayName(root), index, slice);
					return;
				}

				Guid language_guid = LanguageComboBox.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP;
				var decompiler = FindDecompiler(language_guid);
				if (decompiler is null) {
					StatusText.Text = UiText.DecompilerUnavailable(language_guid == DecompilerConstants.LANGUAGE_IL ? "IL" : "C#");
					return;
				}

				current_preview_lines = settings.GetMaximumLines(slice.Nodes.Count);
				var snippets = BuildSnippetPreview(slice, decompiler.GenericGuid, out _);
				GraphSurface.Render(slice, snippets);
				visible_snippets_enabled = true;
				SetVisibleMethods(slice.Nodes.Select(node => node.Method));
				StatusText.Text = FormatGraphStatus(CallGraphIndex.DisplayName(root), index, slice);
				RequestVisibleSnippets();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				if (CurrentViewMode == GraphViewMode.FunctionMap)
					FunctionMapSurface.Clear();
				else
					GraphSurface.Clear(UiText.RenderFailure(ex.Message));
				StatusText.Text = UiText.GraphError(ex.Message);
			}
			finally {
				if (!cancellation_token.IsCancellationRequested) {
					analyzing_graph = false;
					SetBusy(false, null);
					FlushWorkspaceUpdate();
				}
			}
		}

		void FlushWorkspaceUpdate() {
			if (!workspace_update_pending || analyzing_graph || visible_snippets_running || disposed) return;
			workspace_update_pending = false;
			current_index = null; current_index_covers_all_loaded_modules = false;
			document_change_timer.Stop(); document_change_timer.Start();
		}

		static string FormatGraphStatus(string title, CallGraphIndex index, CallGraphSlice slice) =>
			(index.IsReachableIndex ? UiText.ReachableGraphStatus(title, slice.Nodes.Count, slice.Links.Count, slice.IsTruncated) :
			UiText.IndexedGraphStatus(title, slice.Nodes.Count, slice.Links.Count, index.ModuleCount, index.MethodCount, index.LinkCount, slice.IsTruncated)) +
			UiText.UnresolvedCalls(slice.UnresolvedIndirectCallCount >= 0 ? slice.UnresolvedIndirectCallCount : slice.Nodes.Sum(node => index.UnresolvedCalliSites(node.Method).Count)) + AnalysisDetails.Status(slice);

		async void RequestVisibleSnippets() {
			if (disposed || !visible_snippets_enabled || CurrentViewMode != GraphViewMode.CallBlocks) return;
			visible_snippets_requested = true;
			if (visible_snippets_running) return;
			visible_snippets_running = true;
			var token = operation_cancellation?.Token ?? CancellationToken.None;
			try {
				var decompiler = FindDecompiler(LanguageComboBox.SelectedIndex == 1 ? DecompilerConstants.LANGUAGE_IL : DecompilerConstants.LANGUAGE_CSHARP);
				if (decompiler is null) return;
				int maximum_lines = current_preview_lines;
				var decompilation_context = new DecompilationContext { AsyncMethodBodyDecompilation = false };
				while (visible_snippets_enabled && CurrentViewMode == GraphViewMode.CallBlocks) {
					token.ThrowIfCancellationRequested();
					visible_snippets_requested = false;
					var visible = GraphSurface.VisibleMethods();
					MethodDef[] missing;
					lock (snippet_cache)
						missing = visible.Where(method => !snippet_cache.ContainsKey(new SnippetCacheKey(method, decompiler.GenericGuid, maximum_lines))).Take(4).ToArray();
					if (missing.Length == 0) break;
					var snippets = await Task.Run(() => {
						var result = new Dictionary<MethodDef, HighlightedSnippet>();
						var batch_time = Stopwatch.StartNew();
						foreach (var method in missing) {
							token.ThrowIfCancellationRequested();
							result[method] = DecompileWithContext(method, decompiler, maximum_lines, token, decompilation_context);
							if (batch_time.ElapsedMilliseconds >= 60) break;
						}
						return result;
					}, token);
					token.ThrowIfCancellationRequested();
					lock (snippet_cache)
						foreach (var pair in snippets) snippet_cache[new SnippetCacheKey(pair.Key, decompiler.GenericGuid, maximum_lines)] = pair.Value;
					GraphSurface.UpdateSnippets(snippets);
					await Dispatcher.Yield(DispatcherPriority.Background);
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { if (!disposed) StatusText.Text = UiText.GraphError(ex.Message); }
			finally {
				visible_snippets_running = false;
				FlushWorkspaceUpdate();
				if (visible_snippets_requested && !disposed && visible_snippets_enabled)
					_ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RequestVisibleSnippets));
			}
		}

		Dictionary<MethodDef, HighlightedSnippet> BuildSnippetPreview(CallGraphSlice slice, Guid language,
			out bool complete) {
			var result = new Dictionary<MethodDef, HighlightedSnippet>();
			complete = true;
			int maximum_lines = current_preview_lines;
			lock (snippet_cache) {
				foreach (var node in slice.Nodes) {
					var key = new SnippetCacheKey(node.Method, language, maximum_lines);
					if (snippet_cache.TryGetValue(key, out var cached))
						result[node.Method] = cached.LimitLines(maximum_lines);
					else {
						complete = false;
						result[node.Method] = HighlightedSnippet.Plain(UiText.LoadingCode,
							dnSpy.Contracts.Text.TextColor.Gray);
					}
				}
			}
			return result;
		}

		static HighlightedSnippet Decompile(MethodDef method, IDecompiler decompiler, int maximum_lines,
			CancellationToken cancellation_token) => DecompileWithContext(method, decompiler, maximum_lines, cancellation_token,
				new DecompilationContext { AsyncMethodBodyDecompilation = false });

		static HighlightedSnippet DecompileWithContext(MethodDef method, IDecompiler decompiler, int maximum_lines,
			CancellationToken cancellation_token, DecompilationContext context) {
			var output = new HighlightedDecompilerOutput(maximum_lines);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation_token);
			timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
			try {
				context.CancellationToken = timeout.Token;
				try {
					lock (decompiler) {
						timeout.Token.ThrowIfCancellationRequested();
						decompiler.Decompile(method, output, context);
					}
				}
				catch (SnippetLimitReachedException) { }
				return output.CreateSnippet();
			}
			catch (OperationCanceledException) when (!cancellation_token.IsCancellationRequested) {
				return ControlFlowGraph.MethodPreview(method, maximum_lines);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) {
				return HighlightedSnippet.Plain(UiText.Pick("Decompilation failed: ", "Ошибка декомпиляции: ") + ex.Message,
					dnSpy.Contracts.Text.TextColor.Error);
			}
		}

		async void RequestFunctionMapPreview(MethodDef? method) {
			if (method is null || disposed || CurrentViewMode != GraphViewMode.FunctionMap)
				return;
			preview_cancellation?.Cancel();
			preview_cancellation?.Dispose();
			preview_cancellation = new CancellationTokenSource();
			var cancellation_token = preview_cancellation.Token;
			Guid language_guid = LanguageComboBox.SelectedIndex == 1
				? DecompilerConstants.LANGUAGE_IL
				: DecompilerConstants.LANGUAGE_CSHARP;
			var decompiler = FindDecompiler(language_guid);
			if (decompiler is null)
				return;
			const int preview_lines = 24;
			var key = new SnippetCacheKey(method, decompiler.GenericGuid, preview_lines);
			HighlightedSnippet? snippet;
			lock (snippet_cache) {
				snippet_cache.TryGetValue(key, out snippet);
				if (snippet is null)
					snippet = snippet_cache
						.Where(pair => ReferenceEquals(pair.Key.Definition, method) && pair.Key.Language == decompiler.GenericGuid)
						.Select(pair => pair.Value.LimitLines(preview_lines))
						.FirstOrDefault();
			}
			try {
				if (snippet is null) {
					snippet = await Task.Run(() => Decompile(method, decompiler, preview_lines, cancellation_token), cancellation_token);
					lock (snippet_cache)
						snippet_cache[key] = snippet;
				}
				cancellation_token.ThrowIfCancellationRequested();
				if (!disposed && CurrentViewMode == GraphViewMode.FunctionMap)
					FunctionMapSurface.SetCodePreview(method, snippet);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) {
				if (!disposed) FunctionMapSurface.SetCodePreview(method, HighlightedSnippet.Plain(ex.Message, dnSpy.Contracts.Text.TextColor.Error));
			}
		}

		IDecompiler? FindDecompiler(Guid generic_guid) => decompiler_service.AllDecompilers
			.Where(item => item.GenericGuid == generic_guid).OrderBy(item => item.OrderUI).FirstOrDefault();

		async Task<List<CallGraphIndex>> GetOrBuildModuleIndexesAsync(IReadOnlyList<ModuleDef> modules,
			CancellationToken cancellation_token) {
			var available = new Dictionary<ModuleDef, CallGraphIndex>();
			var missing = new List<ModuleDef>();
			foreach (var module in modules) {
				if (index_cache.TryGetValue(module, out var cached))
					available[module] = cached;
				else
					missing.Add(module);
			}
			if (missing.Count != 0) {
				int worker_limit = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
				int worker_count = Math.Min(worker_limit, missing.Count);
				SetBusy(true, UiText.IndexingWorkspaceParallel(missing.Count, modules.Count, worker_count));
				using var semaphore = new SemaphoreSlim(worker_count, worker_count);
				var tasks = missing.Select(async module => {
					await semaphore.WaitAsync(cancellation_token).ConfigureAwait(false);
					try {
						var built = await Task.Run(() => CallGraphIndex.Build(module, cancellation_token), cancellation_token).ConfigureAwait(false);
						return (Module: module, Index: built);
					}
					finally { semaphore.Release(); }
				}).ToArray();
				var built_indexes = await Task.WhenAll(tasks);
				cancellation_token.ThrowIfCancellationRequested();
				foreach (var pair in built_indexes) {
					index_cache.Store(pair.Index);
					available[pair.Module] = pair.Index;
				}
			}
			return modules.Select(module => available[module]).ToList();
		}

		List<ModuleDef> GetLoadedModules() => document_tab_service.DocumentTreeView.DocumentService.GetDocuments()
			.GetModules<ModuleDef>()
			.Distinct()
			.OrderBy(module => module.Name?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
			.ToList();

		internal static MethodDef? GetCurrentMethod(IDocumentTabService document_tab_service) {
			var viewer = document_tab_service.ActiveTab?.TryGetDocumentViewer();
			var method = ResolveMethod(viewer?.SelectedReference?.Data.Reference);
			if (method is not null)
				return method;
			foreach (var node in document_tab_service.ActiveTab?.Content.Nodes ?? Enumerable.Empty<DocumentTreeNodeData>()) {
				if (node is IMDTokenNode token_node && ResolveMethod(token_node.Reference) is MethodDef tab_method)
					return tab_method;
			}
			foreach (var node in document_tab_service.DocumentTreeView.TreeView.TopLevelSelection) {
				if (node is IMDTokenNode token_node && ResolveMethod(token_node.Reference) is MethodDef tree_method)
					return tree_method;
			}
			return null;
		}

		internal static MethodDef? ResolveMethod(object? reference) {
			if (reference is MethodDef definition)
				return definition;
			if (reference is IMethod method) {
				try { return method.ResolveMethodDef(); }
				catch (Exception) { return null; }
			}
			return null;
		}

		internal void DisposeGraph() {
			if (disposed)
				return;
			disposed = true;
			graph_modules = null; workspace_update_pending = false;
			settings.Changed -= Settings_Changed;
			theme_service.ThemeChanged -= ThemeService_ThemeChanged;
			document_tab_service.DocumentModified -= DocumentTabService_DocumentModified;
			document_tab_service.DocumentCollectionChanged -= DocumentTabService_DocumentCollectionChanged;
			document_change_timer.Stop();
			document_change_timer.Tick -= DocumentChangeTimer_Tick;
			operation_cancellation?.Cancel();
			operation_cancellation?.Dispose();
			operation_cancellation = null;
			preview_cancellation?.Cancel();
			preview_cancellation?.Dispose();
			preview_cancellation = null;
			lock (snippet_cache)
				snippet_cache.Clear();
			history.Clear();
			visible_methods.Clear();
			search_matches.Clear();
			current_root = null;
			current_index = null;
			current_index_covers_all_loaded_modules = false;
			GraphSurface.Clear();
			FunctionMapSurface.Clear();
		}

		void AddHistory(MethodDef method) {
			if (history_position >= 0 && history_position < history.Count && ReferenceEquals(history[history_position], method))
				return;
			if (history_position + 1 < history.Count)
				history.RemoveRange(history_position + 1, history.Count - history_position - 1);
			history.Add(method);
			history_position = history.Count - 1;
			UpdateHistoryButtons();
		}

		void NavigateHistory(int delta) {
			int next = history_position + delta;
			if (next < 0 || next >= history.Count)
				return;
			history_position = next;
			current_root = history[history_position];
			if (current_index is not null && !current_index.Contains(current_root)) {
				current_index = null;
				current_index_covers_all_loaded_modules = false;
			}
			UpdateHistoryButtons();
			QueueRender();
		}

		void UpdateHistoryButtons() {
			BackButton.IsEnabled = history_position > 0;
			ForwardButton.IsEnabled = history_position >= 0 && history_position + 1 < history.Count;
		}

		void SetBusy(bool busy, string? message) {
			BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
			if (!string.IsNullOrEmpty(message))
				StatusText.Text = message;
		}
	}
}
