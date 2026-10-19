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
		public bool HideAccessors { get; }
		public bool HideConstructors { get; }
		public bool OnlyCurrentNamespace { get; }
		public bool OnlyCurrentType { get; }
		public bool RequireMethodBody { get; }
		public int MinimumCallSiteCount { get; }

		public CallGraphFilterOptions(bool hide_system_methods = false, bool hide_accessors = false,
			bool hide_constructors = false, bool only_current_namespace = false, bool only_current_type = false,
			bool require_method_body = false, int minimum_call_site_count = 1) {
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
			string name = SafeName(method);
			return name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) ||
				name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal) ||
				name.StartsWith("raise_", StringComparison.Ordinal);
		}

		static bool IsConstructor(MethodDef method) {
			string name = SafeName(method);
			return name == ".ctor" || name == ".cctor";
		}

		static bool IsSystemMethod(MethodDef method) {
			string ns = Namespace(method);
			if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
				ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal))
				return true;
			string assembly = SafeAssemblyName(method);
			return assembly.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
				assembly.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
				assembly.Equals("System", StringComparison.OrdinalIgnoreCase) ||
				assembly.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
				assembly.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
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

		public CallGraphLink(MethodDef source, MethodDef target, int call_site_count,
			IReadOnlyList<uint> il_offsets, IReadOnlyList<string> op_codes) {
			Source = source;
			Target = target;
			CallSiteCount = call_site_count;
			IlOffsets = il_offsets;
			OpCodes = op_codes;
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

		public CallGraphSlice(IReadOnlyList<CallGraphNode> nodes,
			IReadOnlyList<CallGraphLink> links, bool is_truncated) {
			Nodes = nodes;
			Links = links;
			IsTruncated = is_truncated;
		}
	}

	sealed class CallGraphIndex {
		sealed class CallSiteAccumulator {
			public int Count;
			public readonly HashSet<uint> Offsets = new HashSet<uint>();
			public readonly HashSet<string> OpCodes = new HashSet<string>(StringComparer.Ordinal);
		}

		readonly Dictionary<MethodDef, CallGraphLink[]> outgoing;
		readonly Dictionary<MethodDef, CallGraphLink[]> incoming;
		readonly HashSet<MethodDef> module_methods;
		readonly HashSet<ModuleDef> modules;
		readonly CallGraphLink[] all_links;

		public ModuleDef? Module { get; }
		public IReadOnlyCollection<ModuleDef> Modules => modules;
		public int ModuleCount => modules.Count;
		public int MethodCount => module_methods.Count;
		public int LinkCount { get; }
		public int SkippedMethodCount { get; }
		public IReadOnlyList<CallGraphLink> AllLinks => all_links;

		CallGraphIndex(ModuleDef? module, HashSet<ModuleDef> modules, HashSet<MethodDef> module_methods,
			Dictionary<MethodDef, CallGraphLink[]> outgoing,
			Dictionary<MethodDef, CallGraphLink[]> incoming, int link_count, int skipped_method_count) {
			Module = module;
			this.modules = modules;
			this.module_methods = module_methods;
			this.outgoing = outgoing;
			this.incoming = incoming;
			LinkCount = link_count;
			SkippedMethodCount = skipped_method_count;
			all_links = outgoing.Values.SelectMany(value => value).Distinct().ToArray();
		}

		public static CallGraphIndex Build(ModuleDef module, CancellationToken cancellation_token = default) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));

			var methods = EnumerateMethods(module, cancellation_token).OrderBy(SafeToken).ToArray();
			var method_set = new HashSet<MethodDef>(methods);
			var call_sites = new Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator>();
			var resolved_methods = new Dictionary<IMethod, MethodDef?>();
			int skipped = 0;

			foreach (var method in methods) {
				cancellation_token.ThrowIfCancellationRequested();
				try {
					if (!method.HasBody || method.Body?.Instructions is null)
						continue;
					foreach (var instruction in method.Body.Instructions) {
						if (!IsCallInstruction(instruction.OpCode.Code) || instruction.Operand is not IMethod reference)
							continue;
						var target = Resolve(reference, resolved_methods);
						if (target is null)
							continue;
						var key = (method, target);
						if (!call_sites.TryGetValue(key, out var accumulator))
							call_sites.Add(key, accumulator = new CallSiteAccumulator());
						accumulator.Count++;
						accumulator.Offsets.Add(instruction.Offset);
						accumulator.OpCodes.Add(instruction.OpCode.Name ?? instruction.OpCode.Code.ToString());
					}
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception) { skipped++; }
			}

			var links = call_sites
				.Select(pair => new CallGraphLink(pair.Key.Source, pair.Key.Target, pair.Value.Count,
					pair.Value.Offsets.OrderBy(offset => offset).ToArray(),
					pair.Value.OpCodes.OrderBy(name => name, StringComparer.Ordinal).ToArray()))
				.OrderBy(link => SafeToken(link.Source))
				.ThenBy(link => DisplayName(link.Target), StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var outgoing = links.GroupBy(link => link.Source)
				.ToDictionary(group => group.Key, group => group.ToArray());
			var incoming = links.GroupBy(link => link.Target)
				.ToDictionary(group => group.Key, group => group.ToArray());
			return new CallGraphIndex(module, new HashSet<ModuleDef> { module }, method_set,
				outgoing, incoming, links.Length, skipped);
		}

		public static CallGraphIndex Combine(IEnumerable<CallGraphIndex> module_indexes) {
			if (module_indexes is null)
				throw new ArgumentNullException(nameof(module_indexes));
			var indexes = module_indexes
				.Where(index => index is not null)
				.SelectMany(index => index.Module is ModuleDef module
					? new[] { (Module: module, Index: index) }
					: Array.Empty<(ModuleDef Module, CallGraphIndex Index)>())
				.GroupBy(pair => pair.Module)
				.Select(group => group.Last().Index)
				.ToArray();
			if (indexes.Length == 0)
				throw new ArgumentException("At least one single-module index is required.", nameof(module_indexes));
			if (indexes.Length == 1)
				return indexes[0];

			var modules = new HashSet<ModuleDef>(indexes.Select(index => index.Module!));
			var methods = new HashSet<MethodDef>(indexes.SelectMany(index => index.module_methods));
			var links = indexes.SelectMany(index => index.all_links).Distinct().ToArray();
			var outgoing = links.GroupBy(link => link.Source)
				.ToDictionary(group => group.Key, group => group.ToArray());
			var incoming = links.GroupBy(link => link.Target)
				.ToDictionary(group => group.Key, group => group.ToArray());
			return new CallGraphIndex(null, modules, methods, outgoing, incoming, links.Length,
				indexes.Sum(index => index.SkippedMethodCount));
		}

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

		static bool IsCallInstruction(Code code) =>
			code == Code.Call || code == Code.Callvirt || code == Code.Newobj || code == Code.Jmp ||
			code == Code.Ldftn || code == Code.Ldvirtftn;

		static MethodDef? Resolve(IMethod reference, Dictionary<IMethod, MethodDef?> cache) {
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

		static uint SafeToken(MethodDef method) {
			try { return method.MDToken.Raw; }
			catch (Exception) { return uint.MaxValue; }
		}

		public bool Contains(MethodDef method) => module_methods.Contains(method);
		public bool ContainsModule(ModuleDef module) => modules.Contains(module);

		public IReadOnlyList<CallGraphLink> Outgoing(MethodDef method) =>
			outgoing.TryGetValue(method, out var links) ? links : Array.Empty<CallGraphLink>();

		public IReadOnlyList<CallGraphLink> Incoming(MethodDef method) =>
			incoming.TryGetValue(method, out var links) ? links : Array.Empty<CallGraphLink>();

		public CallGraphSlice Select(MethodDef root, CallGraphDirection direction, int depth, int maximum_nodes,
			CallGraphFilterOptions? filter_options = null) {
			depth = Math.Max(1, Math.Min(8, depth));
			maximum_nodes = Math.Max(2, maximum_nodes);
			var filter = filter_options ?? CallGraphFilterOptions.None;
			var layers = new Dictionary<MethodDef, int> { [root] = 0 };
			bool truncated = false;

			if (direction != CallGraphDirection.Callers)
				Traverse(root, depth, maximum_nodes, +1, outgoing, link => link.Target, filter, layers, ref truncated);
			if (direction != CallGraphDirection.Callees)
				Traverse(root, depth, maximum_nodes, -1, incoming, link => link.Source, filter, layers, ref truncated);

			var visible = new HashSet<MethodDef>(layers.Keys);
			var nodes = layers.Select(pair => new CallGraphNode(pair.Key, pair.Value, pair.Key == root,
				!module_methods.Contains(pair.Key)))
				.OrderBy(node => node.Layer)
				.ThenBy(node => DisplayName(node.Method), StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var links = visible.SelectMany(method => outgoing.TryGetValue(method, out var method_links)
					? method_links : Array.Empty<CallGraphLink>())
				.Where(link => visible.Contains(link.Target) &&
					link.CallSiteCount >= filter.MinimumCallSiteCount)
				.OrderBy(link => layers[link.Source])
				.ThenBy(link => DisplayName(link.Source), StringComparer.OrdinalIgnoreCase)
				.ThenBy(link => DisplayName(link.Target), StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return new CallGraphSlice(nodes, links, truncated);
		}

		static void Traverse(MethodDef root, int maximum_depth, int maximum_nodes, int layer_sign,
			Dictionary<MethodDef, CallGraphLink[]> adjacency, Func<CallGraphLink, MethodDef> select_target,
			CallGraphFilterOptions filter, Dictionary<MethodDef, int> layers, ref bool truncated) {
			var visited = new HashSet<MethodDef> { root };
			var queue = new Queue<(MethodDef Method, int Depth)>();
			queue.Enqueue((root, 0));
			while (queue.Count != 0) {
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
					if (!layers.ContainsKey(next))
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
				string parameters = method.Parameters.Count == 0
					? string.Empty
					: string.Join(", ", method.Parameters.Where(parameter => !parameter.IsHiddenThisParameter)
						.Select(parameter => parameter.Type?.TypeName ?? "?"));
				return $"{DisplayName(method)}({parameters})";
			}
			catch (Exception) { return DisplayName(method) + "(?)"; }
		}
	}

	sealed class CallGraphIndexCache {
		readonly Dictionary<ModuleDef, CallGraphIndex> entries = new Dictionary<ModuleDef, CallGraphIndex>();

		public int Count => entries.Count;
		public bool TryGetValue(ModuleDef module, out CallGraphIndex index) => entries.TryGetValue(module, out index!);
		public void Store(CallGraphIndex index) {
			if (index.Module is not ModuleDef module)
				throw new ArgumentException("Only single-module indexes can be cached.", nameof(index));
			entries[module] = index;
		}
		public bool Invalidate(ModuleDef module) => entries.Remove(module);
		public int Invalidate(IEnumerable<ModuleDef> modules) {
			int count = 0;
			foreach (var module in modules.Distinct())
				if (entries.Remove(module))
					count++;
			return count;
		}
		public int RemoveMissing(ISet<ModuleDef> loaded_modules) {
			int count = 0;
			foreach (var module in entries.Keys.Where(module => !loaded_modules.Contains(module)).ToArray())
				if (entries.Remove(module))
					count++;
			return count;
		}
		public void Clear() => entries.Clear();
	}
}
