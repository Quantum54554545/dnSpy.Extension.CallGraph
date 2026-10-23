using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	enum CallGraphDirection {
		Callees,
		Callers,
		Both,
	}

	sealed class CallGraphFilterOptions {
		public static readonly CallGraphFilterOptions None = new CallGraphFilterOptions();

		public bool HideSystemMethods { get; }
		public bool OnlyCurrentAssembly { get; }
		public bool HideAccessors { get; }
		public bool HideConstructors { get; }
		public bool OnlyCurrentNamespace { get; }
		public bool OnlyCurrentType { get; }
		public bool RequireMethodBody { get; }
		public int MinimumCallSiteCount { get; }

		public CallGraphFilterOptions(bool hide_system_methods = false, bool hide_accessors = false,
			bool hide_constructors = false, bool only_current_namespace = false, bool only_current_type = false,
			bool require_method_body = false, int minimum_call_site_count = 1, bool only_current_assembly = false) {
			OnlyCurrentAssembly = only_current_assembly;
			HideSystemMethods = hide_system_methods;
			HideAccessors = hide_accessors;
			HideConstructors = hide_constructors;
			OnlyCurrentNamespace = only_current_namespace;
			OnlyCurrentType = only_current_type;
			RequireMethodBody = require_method_body;
			MinimumCallSiteCount = Math.Max(1, minimum_call_site_count);
		}

		public bool Includes(MethodDef root, MethodDef method) {
			if (ReferenceEquals(root, method))
				return true;
			if (OnlyCurrentAssembly && !ReferenceEquals(root.Module, method.Module) && (root.Module.Assembly is null || !ReferenceEquals(root.Module.Assembly, method.Module.Assembly)))
				return false;
			if (HideSystemMethods && IsSystemMethod(method))
				return false;
			if (HideAccessors && IsAccessor(method))
				return false;
			if (HideConstructors && IsConstructor(method))
				return false;
			if (RequireMethodBody && !SafeHasBody(method))
				return false;
			if (OnlyCurrentType && !ReferenceEquals(root.DeclaringType, method.DeclaringType))
				return false;
			if (OnlyCurrentNamespace && !StringComparer.Ordinal.Equals(Namespace(root), Namespace(method)))
				return false;
			return true;
		}

		public bool Includes(MethodDef root, CallGraphLink link, MethodDef adjacent) =>
			link.CallSiteCount >= MinimumCallSiteCount && Includes(root, adjacent);

		static bool IsAccessor(MethodDef method) {
			return method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn || method.IsFire;
		}

		static bool IsConstructor(MethodDef method) {
			string name = SafeName(method);
			return name == ".ctor" || name == ".cctor";
		}

		static bool IsSystemMethod(MethodDef method) {
			return FrameworkIdentity.IsFrameworkType(method.DeclaringType);
		}

		static string Namespace(MethodDef method) {
			try { return method.DeclaringType?.Namespace?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static string SafeAssemblyName(MethodDef method) {
			try { return method.Module?.Assembly?.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static string SafeName(MethodDef method) {
			try { return method.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static bool SafeHasBody(MethodDef method) {
			try { return method.HasBody; }
			catch (Exception) { return false; }
		}
	}

	sealed class CallGraphLink {
		public MethodDef Source { get; }
		public MethodDef Target { get; }
		public int CallSiteCount { get; }
		public IReadOnlyList<uint> IlOffsets { get; }
		public IReadOnlyList<string> OpCodes { get; }
		public bool IsPotential { get; }

		public CallGraphLink(MethodDef source, MethodDef target, int call_site_count,
			IReadOnlyList<uint> il_offsets, IReadOnlyList<string> op_codes, bool is_potential = false) {
			Source = source;
			Target = target;
			CallSiteCount = call_site_count;
			IlOffsets = il_offsets;
			OpCodes = op_codes;
			IsPotential = is_potential;
		}
	}

	sealed class CallGraphNode {
		public MethodDef Method { get; }
		public int Layer { get; }
		public bool IsRoot { get; }
		public bool IsExternal { get; }

		public CallGraphNode(MethodDef method, int layer, bool is_root, bool is_external) {
			Method = method;
			Layer = layer;
			IsRoot = is_root;
			IsExternal = is_external;
		}
	}

	sealed class CallGraphSlice {
		public IReadOnlyList<CallGraphNode> Nodes { get; }
		public IReadOnlyList<CallGraphLink> Links { get; }
		public bool IsTruncated { get; }
		public int UnresolvedIndirectCallCount { get; }
		public IReadOnlyList<GraphDiagnostic> Diagnostics { get; }
		readonly Dictionary<MethodDef, GraphDiagnostic[]> diagnostics_by_method;
		public IReadOnlyList<GraphDiagnostic> DiagnosticsFor(MethodDef method) => diagnostics_by_method.TryGetValue(method, out var values) ? values : Array.Empty<GraphDiagnostic>();

		public CallGraphSlice(IReadOnlyList<CallGraphNode> nodes,
			IReadOnlyList<CallGraphLink> links, bool is_truncated) : this(nodes, links, is_truncated, -1) { }
		public CallGraphSlice(IReadOnlyList<CallGraphNode> nodes,
			IReadOnlyList<CallGraphLink> links, bool is_truncated, int unresolved_indirect_call_count, IReadOnlyList<GraphDiagnostic>? diagnostics = null) {
			Nodes = nodes;
			Links = links;
			IsTruncated = is_truncated;
			UnresolvedIndirectCallCount = unresolved_indirect_call_count;
			Diagnostics = diagnostics ?? Array.Empty<GraphDiagnostic>();
			diagnostics_by_method = Diagnostics.GroupBy(item => item.Method).ToDictionary(group => group.Key, group => group.ToArray());
		}
	}

	sealed class CallSiteAccumulator {
		public int Count;
		public bool HasDirect;
		public readonly HashSet<uint> Offsets = new HashSet<uint>();
		public readonly HashSet<string> Operations = new HashSet<string>(StringComparer.Ordinal);

		public void Add(uint offset, string operation, bool is_potential) {
			if (Offsets.Add(offset))
				Count++;
			Operations.Add(operation);
			if (!is_potential)
				HasDirect = true;
		}

		public void Add(CallGraphLink link) {
			Offsets.UnionWith(link.IlOffsets);
			Operations.UnionWith(link.OpCodes);
			Count = Math.Max(Count, Math.Max(link.CallSiteCount, Offsets.Count));
			if (!link.IsPotential)
				HasDirect = true;
		}
	}

	sealed class VirtualCallSite {
		public MethodDef Source { get; }
		public MethodDef Declaration { get; }
		public uint Offset { get; }
		public string Operation { get; }

		public VirtualCallSite(MethodDef source, MethodDef declaration, uint offset, string operation) {
			Source = source;
			Declaration = declaration;
			Offset = offset;
			Operation = operation;
		}
	}

	sealed class ReflectionTarget {
		public MethodDef Source { get; }
		public IReadOnlyList<string> TypeNames { get; }
		public IReadOnlyList<string> MethodNames { get; }
		public IReadOnlyList<MethodDef> ExactMethods { get; }
		public uint Offset { get; }
		public string Operation { get; }

		public ReflectionTarget(MethodDef source, IEnumerable<string> type_names, IEnumerable<string> method_names,
			IEnumerable<MethodDef> exact_methods, uint offset, string operation) {
			Source = source;
			TypeNames = type_names.Distinct(StringComparer.Ordinal).ToArray();
			MethodNames = method_names.Distinct(StringComparer.Ordinal).ToArray();
			ExactMethods = exact_methods.Distinct().ToArray();
			Offset = offset;
			Operation = operation;
		}
	}

	sealed class CallGraphIndex {
		readonly Dictionary<MethodDef, CallGraphLink[]> outgoing;
		readonly Dictionary<MethodDef, CallGraphLink[]> incoming;
		readonly HashSet<MethodDef> module_methods;
		readonly HashSet<ModuleDef> modules;
		readonly CallGraphLink[] all_links;
		readonly VirtualCallSite[] virtual_call_sites;
		readonly ReflectionTarget[] reflection_targets;
		readonly Dictionary<MethodDef, uint[]> unresolved_calli = new Dictionary<MethodDef, uint[]>();

		public ModuleDef? Module { get; }
		public IReadOnlyCollection<ModuleDef> Modules => modules;
		public int ModuleCount => modules.Count;
		public int MethodCount => module_methods.Count;
		public int LinkCount { get; }
		public int SkippedMethodCount { get; }
		public bool IsReachableIndex { get; }
		public IReadOnlyList<CallGraphLink> AllLinks => all_links;
		IReadOnlyList<GraphDiagnostic> diagnostics = Array.Empty<GraphDiagnostic>();

		CallGraphIndex(ModuleDef? module, HashSet<ModuleDef> modules, HashSet<MethodDef> module_methods,
			Dictionary<MethodDef, CallGraphLink[]> outgoing, Dictionary<MethodDef, CallGraphLink[]> incoming,
			IEnumerable<VirtualCallSite> virtual_call_sites, IEnumerable<ReflectionTarget> reflection_targets,
			int skipped_method_count, bool is_reachable_index = false) {
			IsReachableIndex = is_reachable_index;
			Module = module;
			this.modules = modules;
			this.module_methods = module_methods;
			this.outgoing = outgoing;
			this.incoming = incoming;
			this.virtual_call_sites = virtual_call_sites.ToArray();
			this.reflection_targets = reflection_targets.ToArray();
			SkippedMethodCount = skipped_method_count;
			all_links = outgoing.Values.SelectMany(value => value).Distinct().ToArray();
			LinkCount = all_links.Length;
			foreach (var method in is_reachable_index ? Enumerable.Empty<MethodDef>() : module_methods) {
				try {
					if (!method.HasBody) continue;
					var resolved = outgoing.TryGetValue(method, out var links)
						? new HashSet<uint>(links.Where(link => link.OpCodes.Contains("calli")).SelectMany(link => link.IlOffsets)) : new HashSet<uint>();
					var offsets = method.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Calli && !resolved.Contains(instruction.Offset))
						.Select(instruction => instruction.Offset).ToArray();
					if (offsets.Length != 0) unresolved_calli.Add(method, offsets);
				}
				catch (Exception) { }
			}
		}

		public static (CallGraphIndex Index, CallGraphSlice Slice) BuildReachable(MethodDef root,
			IReadOnlyList<ModuleDef> loaded_modules, int depth, int maximum_nodes, CallGraphFilterOptions filters,
			CancellationToken cancellation_token = default) => BuildReachableProgressive(root, loaded_modules, depth, maximum_nodes, filters, null, cancellation_token);
		public static (CallGraphIndex Index, CallGraphSlice Slice) BuildReachableProgressive(MethodDef root,
			IReadOnlyList<ModuleDef> loaded_modules, int depth, int maximum_nodes, CallGraphFilterOptions filters,
			IProgress<CallGraphSlice>? progress, CancellationToken cancellation_token = default) {
			var modules = new HashSet<ModuleDef>(loaded_modules) { root.Module };
			var slice = new ReachableCallAnalysis(modules, cancellation_token).Select(root, depth, maximum_nodes, filters, progress);
			var index = new CallGraphIndex(root.Module, modules,
				new HashSet<MethodDef>(slice.Nodes.Where(node => !node.IsExternal).Select(node => node.Method)),
				slice.Links.GroupBy(link => link.Source).ToDictionary(group => group.Key, group => group.ToArray()),
				slice.Links.GroupBy(link => link.Target).ToDictionary(group => group.Key, group => group.ToArray()),
				Array.Empty<VirtualCallSite>(), Array.Empty<ReflectionTarget>(), 0, true);
			return (index, slice);
		}

		public static CallGraphIndex Build(ModuleDef module, CancellationToken cancellation_token = default) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var methods = EnumerateMethods(module, cancellation_token).OrderBy(SafeToken).ToArray();
			var call_sites = new Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator>();
			var virtual_sites = new List<VirtualCallSite>();
			var reflection_sites = new List<ReflectionTarget>();
			var resolved_methods = new Dictionary<IMethod, MethodDef?>();
			var safety_notes = new List<GraphDiagnostic>();
			int skipped = 0;
			foreach (var method in methods) {
				cancellation_token.ThrowIfCancellationRequested();
				try {
					if (!method.HasBody || method.Body?.Instructions is null)
						continue;
					if (method.Body.Instructions.Count > 200000) { skipped++; safety_notes.Add(new GraphDiagnostic(method, 0, GraphDiagnosticKind.AnalysisBudget, "method instruction limit reached")); continue; }
					if (ReachableCallAnalysis.NeedsFlow(method)) continue;
					if (IlStackValidation.Check(method, cancellation_token) is string failure) { skipped++; safety_notes.Add(new GraphDiagnostic(method, 0, failure.StartsWith("invalid IL", StringComparison.Ordinal) ? GraphDiagnosticKind.InvalidIl : GraphDiagnosticKind.AnalysisBudget, failure)); continue; }
					var reached = IlReachability.Find(method, cancellation_token);
					foreach (var instruction in method.Body.Instructions) {
						if (reached is not null && !reached.Contains(instruction.Offset)) continue;
						if (!IsDirectCallInstruction(instruction.OpCode.Code) || instruction.Operand is not IMethod reference)
							continue;
						var target = Resolve(reference, resolved_methods);
						if (target is null)
							continue;
						AddCallSite(call_sites, method, target, instruction.Offset,
							instruction.OpCode.Name ?? instruction.OpCode.Code.ToString(), false);
						if (instruction.OpCode.Code == Code.Callvirt && IsPolymorphic(target))
							virtual_sites.Add(new VirtualCallSite(method, target, instruction.Offset, "possible callvirt target"));
					}
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { skipped++; safety_notes.Add(new GraphDiagnostic(method, 0, GraphDiagnosticKind.InvalidIl, "IL scan failed: " + error.GetType().Name)); }
			}
			var analysis = new ReachableCallAnalysis(new[] { module }, cancellation_token);
			skipped += analysis.ScanIndex(methods, call_sites, virtual_sites, reflection_sites);
			var index = Create(module, new HashSet<ModuleDef> { module }, new HashSet<MethodDef>(methods), call_sites, virtual_sites, reflection_sites, skipped);
			index.diagnostics = safety_notes.Concat(analysis.Diagnostics).ToArray(); return index;
		}

		public static CallGraphIndex Combine(IEnumerable<CallGraphIndex> module_indexes) {
			if (module_indexes is null)
				throw new ArgumentNullException(nameof(module_indexes));
			var indexes = module_indexes.Where(index => index?.Module is not null)
				.GroupBy(index => index.Module!)
				.Select(group => group.Last())
				.ToArray();
			if (indexes.Length == 0)
				throw new ArgumentException("At least one single-module index is required.", nameof(module_indexes));
			var modules = new HashSet<ModuleDef>(indexes.Select(index => index.Module!));
			var methods = new HashSet<MethodDef>(indexes.SelectMany(index => index.module_methods));
			var call_sites = new Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator>();
			foreach (var link in indexes.SelectMany(index => index.all_links)) {
				var key = (link.Source, link.Target);
				if (!call_sites.TryGetValue(key, out var accumulator))
					call_sites.Add(key, accumulator = new CallSiteAccumulator());
				accumulator.Add(link);
			}
			WorkspaceCallResolver.AddExtendedEdges(methods, indexes.SelectMany(index => index.virtual_call_sites),
				indexes.SelectMany(index => index.reflection_targets), call_sites);
			var combined = Create(indexes.Length == 1 ? indexes[0].Module : null, modules, methods, call_sites,
				Array.Empty<VirtualCallSite>(), Array.Empty<ReflectionTarget>(), indexes.Sum(index => index.SkippedMethodCount));
			combined.diagnostics = indexes.SelectMany(index => index.diagnostics).ToArray(); return combined;
		}

		static CallGraphIndex Create(ModuleDef? module, HashSet<ModuleDef> modules, HashSet<MethodDef> methods,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites,
			IEnumerable<VirtualCallSite> virtual_sites, IEnumerable<ReflectionTarget> reflection_sites, int skipped) {
			var links = call_sites.Select(pair => new CallGraphLink(pair.Key.Source, pair.Key.Target,
				pair.Value.Count, pair.Value.Offsets.OrderBy(offset => offset).ToArray(),
				pair.Value.Operations.OrderBy(value => value, StringComparer.Ordinal).ToArray(), !pair.Value.HasDirect))
				.OrderBy(link => SafeToken(link.Source))
				.ThenBy(link => DisplayName(link.Target), StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return new CallGraphIndex(module, modules, methods,
				links.GroupBy(link => link.Source).ToDictionary(group => group.Key, group => group.ToArray()),
				links.GroupBy(link => link.Target).ToDictionary(group => group.Key, group => group.ToArray()),
				virtual_sites, reflection_sites, skipped);
		}

		internal static void AddCallSite(Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites,
			MethodDef source, MethodDef target, uint offset, string operation, bool is_potential) {
			var key = (source, target);
			if (!call_sites.TryGetValue(key, out var accumulator))
				call_sites.Add(key, accumulator = new CallSiteAccumulator());
			accumulator.Add(offset, operation, is_potential);
		}

		internal static MethodDef? Resolve(IMethod reference, Dictionary<IMethod, MethodDef?> cache) {
			if (reference is MethodDef definition)
				return definition;
			if (cache.TryGetValue(reference, out var cached))
				return cached;
			MethodDef? result;
			try { result = reference.ResolveMethodDef(); }
			catch (Exception) { result = null; }
			cache[reference] = result;
			return result;
		}

		internal static string SafeTypeName(ITypeDefOrRef? type) {
			try { return type?.FullName ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		internal static string SafeMethodName(MethodDef method) {
			try { return method.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		internal static uint SafeToken(MethodDef method) {
			try { return method.MDToken.Raw; }
			catch (Exception) { return uint.MaxValue; }
		}

		static bool IsPolymorphic(MethodDef method) {
			try { return method.IsVirtual || method.DeclaringType?.IsInterface == true; }
			catch (Exception) { return false; }
		}

		static bool IsDirectCallInstruction(Code code) =>
			code == Code.Call || code == Code.Callvirt || code == Code.Newobj || code == Code.Jmp;

		static IEnumerable<MethodDef> EnumerateMethods(ModuleDef module, CancellationToken cancellation_token) {
			TypeDef[] roots;
			try { roots = module.Types.ToArray(); }
			catch (Exception) { yield break; }
			var stack = new Stack<TypeDef>(roots.Reverse());
			while (stack.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var type = stack.Pop();
				MethodDef[] methods;
				try { methods = type.Methods.ToArray(); }
				catch (Exception) { methods = Array.Empty<MethodDef>(); }
				foreach (var method in methods)
					yield return method;
				try {
					var nested = type.NestedTypes.ToArray();
					for (int i = nested.Length - 1; i >= 0; i--)
						stack.Push(nested[i]);
				}
				catch (Exception) { }
			}
		}

		public bool Contains(MethodDef method) => module_methods.Contains(method);
		public IReadOnlyList<uint> UnresolvedCalliSites(MethodDef method) =>
			unresolved_calli.TryGetValue(method, out var offsets) ? offsets : Array.Empty<uint>();
		public bool ContainsModule(ModuleDef module) => modules.Contains(module);
		public IReadOnlyList<CallGraphLink> Outgoing(MethodDef method) =>
			outgoing.TryGetValue(method, out var links) ? links : Array.Empty<CallGraphLink>();
		public IReadOnlyList<CallGraphLink> Incoming(MethodDef method) =>
			incoming.TryGetValue(method, out var links) ? links : Array.Empty<CallGraphLink>();

		public CallGraphSlice Select(MethodDef root, CallGraphDirection direction, int depth, int maximum_nodes,
			CallGraphFilterOptions? filter_options = null, CancellationToken cancellation_token = default) {
			cancellation_token.ThrowIfCancellationRequested();
			if (direction == CallGraphDirection.Callees)
				return new ReachableCallAnalysis(modules, cancellation_token).Select(root, depth, maximum_nodes, filter_options ?? CallGraphFilterOptions.None);
			maximum_nodes = Math.Max(2, maximum_nodes);
			depth = Math.Max(1, Math.Min(depth, maximum_nodes - 1));
			var filter = filter_options ?? CallGraphFilterOptions.None;
			var forward = direction == CallGraphDirection.Both
				? new ReachableCallAnalysis(modules, cancellation_token).Select(root, depth, maximum_nodes, filter) : null;
			var layers = forward is null ? new Dictionary<MethodDef, int> { [root] = 0 } : forward.Nodes.ToDictionary(node => node.Method, node => node.Layer);
			bool truncated = forward?.IsTruncated == true;
			if (direction != CallGraphDirection.Callees)
				Traverse(root, depth, maximum_nodes, -1, incoming, link => link.Source, filter, layers, ref truncated, cancellation_token);
			var visible = new HashSet<MethodDef>(layers.Keys);
			var nodes = layers.Select(pair => new CallGraphNode(pair.Key, pair.Value, pair.Key == root,
				!module_methods.Contains(pair.Key)))
				.OrderBy(node => node.Layer)
				.ThenBy(node => DisplayName(node.Method), StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var links = visible.SelectMany(method => outgoing.TryGetValue(method, out var method_links)
					? method_links : Array.Empty<CallGraphLink>())
				.Where(link => visible.Contains(link.Target) && link.CallSiteCount >= filter.MinimumCallSiteCount && (forward is null || layers[link.Source] < 0))
				.Concat(forward?.Links ?? Array.Empty<CallGraphLink>())
				.OrderBy(link => layers[link.Source])
				.ThenBy(link => DisplayName(link.Source), StringComparer.OrdinalIgnoreCase)
				.ThenBy(link => DisplayName(link.Target), StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return new CallGraphSlice(nodes, links, truncated, (forward?.UnresolvedIndirectCallCount ?? 0) +
				layers.Where(pair => forward is null || pair.Value < 0).Sum(pair => UnresolvedCalliSites(pair.Key).Count),
				(forward?.Diagnostics ?? Array.Empty<GraphDiagnostic>()).Concat(diagnostics.Where(item => visible.Contains(item.Method) && (forward is null || layers[item.Method] < 0))).ToArray());
		}

		static void Traverse(MethodDef root, int maximum_depth, int maximum_nodes, int layer_sign,
			Dictionary<MethodDef, CallGraphLink[]> adjacency, Func<CallGraphLink, MethodDef> select_target,
			CallGraphFilterOptions filter, Dictionary<MethodDef, int> layers, ref bool truncated, CancellationToken cancellation_token) {
			var visited = new HashSet<MethodDef> { root };
			var queue = new Queue<(MethodDef Method, int Depth)>();
			queue.Enqueue((root, 0));
			while (queue.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var current = queue.Dequeue();
				if (current.Depth >= maximum_depth || !adjacency.TryGetValue(current.Method, out var links))
					continue;
				foreach (var link in links.OrderBy(item => DisplayName(select_target(item)), StringComparer.OrdinalIgnoreCase)) {
					var next = select_target(link);
					if (!filter.Includes(root, link, next) || !visited.Add(next))
						continue;
					if (layers.Count >= maximum_nodes) {
						truncated = true;
						continue;
					}
					int next_depth = current.Depth + 1;
					layers[next] = next_depth * layer_sign;
					queue.Enqueue((next, next_depth));
				}
			}
		}

		public static string DisplayName(MethodDef method) {
			try {
				string declaring_type = method.DeclaringType?.FullName ?? "<Module>";
				return declaring_type + "::" + (method.Name?.ToString() ?? $"method_{SafeToken(method):X8}");
			}
			catch (Exception) { return $"method_0x{SafeToken(method):X8}"; }
		}

		public static string Signature(MethodDef method) {
			try {
				string parameters = method.Parameters.Count == 0 ? string.Empty :
					string.Join(", ", method.Parameters.Where(parameter => !parameter.IsHiddenThisParameter)
						.Select(parameter => parameter.Type?.TypeName ?? "?"));
				return $"{DisplayName(method)}({parameters})";
			}
			catch (Exception) { return DisplayName(method) + "(?)"; }
		}
	}

	sealed class CallGraphIndexCache {
		readonly object sync = new object();
		readonly Dictionary<ModuleDef, CallGraphIndex> entries = new Dictionary<ModuleDef, CallGraphIndex>();
		CallGraphIndex[] combined_sources = Array.Empty<CallGraphIndex>();
		CallGraphIndex? combined_index;

		public int Count {
			get { lock (sync) return entries.Count; }
		}

		public bool TryGetValue(ModuleDef module, out CallGraphIndex index) {
			lock (sync)
				return entries.TryGetValue(module, out index!);
		}

		public void Store(CallGraphIndex index) {
			if (index.Module is not ModuleDef module)
				throw new ArgumentException("Only single-module indexes can be cached.", nameof(index));
			lock (sync) {
				entries[module] = index;
				ResetCombined();
			}
		}

		public bool TryGetCombined(IReadOnlyList<CallGraphIndex> sources, out CallGraphIndex index) {
			lock (sync) {
				if (combined_index is not null && combined_sources.Length == sources.Count) {
					bool matches = true;
					for (int i = 0; i < combined_sources.Length; i++) {
						if (ReferenceEquals(combined_sources[i], sources[i]))
							continue;
						matches = false;
						break;
					}
					if (matches) {
						index = combined_index;
						return true;
					}
				}
				index = null!;
				return false;
			}
		}

		public void StoreCombined(IReadOnlyList<CallGraphIndex> sources, CallGraphIndex index) {
			lock (sync) {
				combined_sources = sources.ToArray();
				combined_index = index;
			}
		}

		public bool Invalidate(ModuleDef module) {
			lock (sync) {
				bool removed = entries.Remove(module);
				if (removed)
					ResetCombined();
				return removed;
			}
		}

		public int Invalidate(IEnumerable<ModuleDef> modules) {
			lock (sync) {
				int count = 0;
				foreach (var module in modules.Distinct())
					if (entries.Remove(module))
						count++;
				if (count != 0)
					ResetCombined();
				return count;
			}
		}

		public int RemoveMissing(ISet<ModuleDef> loaded_modules) {
			lock (sync) {
				int count = 0;
				foreach (var module in entries.Keys.Where(module => !loaded_modules.Contains(module)).ToArray())
					if (entries.Remove(module))
						count++;
				if (count != 0)
					ResetCombined();
				return count;
			}
		}

		public void Clear() {
			lock (sync) {
				entries.Clear();
				ResetCombined();
			}
		}

		void ResetCombined() {
			combined_sources = Array.Empty<CallGraphIndex>();
			combined_index = null;
		}
	}
}
