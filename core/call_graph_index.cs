using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	enum call_graph_direction {
		callees,
		callers,
		both,
	}

	sealed class call_graph_filter_options {
		public static readonly call_graph_filter_options none = new call_graph_filter_options();

		public bool hide_system_methods { get; }
		public bool only_current_assembly { get; }
		public bool hide_accessors { get; }
		public bool hide_constructors { get; }
		public bool only_current_namespace { get; }
		public bool only_current_type { get; }
		public bool require_method_body { get; }
		public int minimum_call_site_count { get; }
		public bool preserve_structural_calls { get; }
		public bool thorough_refinement { get; }

		public call_graph_filter_options(bool hide_system_methods = false, bool hide_accessors = false,
			bool hide_constructors = false, bool only_current_namespace = false, bool only_current_type = false,
			bool require_method_body = false, int minimum_call_site_count = 1, bool only_current_assembly = false, bool preserve_structural_calls = true, bool thorough_refinement = false) {
			this.preserve_structural_calls = preserve_structural_calls;
			this.thorough_refinement = thorough_refinement;
			this.only_current_assembly = only_current_assembly;
			this.hide_system_methods = hide_system_methods;
			this.hide_accessors = hide_accessors;
			this.hide_constructors = hide_constructors;
			this.only_current_namespace = only_current_namespace;
			this.only_current_type = only_current_type;
			this.require_method_body = require_method_body;
			this.minimum_call_site_count = Math.Max(1, minimum_call_site_count);
		}

		public bool includes(MethodDef root, MethodDef method) {
			if (ReferenceEquals(root, method))
				return true;
			if (this.only_current_assembly && !ReferenceEquals(root.Module, method.Module) && (root.Module.Assembly is null || !ReferenceEquals(root.Module.Assembly, method.Module.Assembly)))
				return false;
			if (this.hide_system_methods && call_graph_filter_options.is_system_method(method))
				return false;
			if (this.hide_accessors && call_graph_filter_options.is_accessor(method))
				return false;
			if (this.hide_constructors && call_graph_filter_options.is_constructor(method))
				return false;
			if (this.require_method_body && !call_graph_filter_options.safe_has_body(method))
				return false;
			if (this.only_current_type && !ReferenceEquals(root.DeclaringType, method.DeclaringType))
				return false;
			if (this.only_current_namespace && !StringComparer.Ordinal.Equals(call_graph_filter_options.@namespace(root), call_graph_filter_options.@namespace(method)))
				return false;
			return true;
		}

		public bool includes(MethodDef root, call_graph_link link, MethodDef adjacent) =>
			link.call_site_count >= this.minimum_call_site_count && this.includes(root, adjacent);

		static bool is_accessor(MethodDef method) {
			return method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn || method.IsFire;
		}

		static bool is_constructor(MethodDef method) {
			string name = call_graph_filter_options.safe_name(method);
			return name == ".ctor" || name == ".cctor";
		}

		static bool is_system_method(MethodDef method) {
			return framework_identity.is_framework_type(method.DeclaringType);
		}

		static string @namespace(MethodDef method) {
			try { var type = method.DeclaringType; while (type?.DeclaringType is TypeDef parent) type = parent; return type?.Namespace?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static string safe_assembly_name(MethodDef method) {
			try { return method.Module?.Assembly?.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static string safe_name(MethodDef method) {
			try { return method.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		static bool safe_has_body(MethodDef method) {
			try { return method.HasBody; }
			catch (Exception) { return false; }
		}
	}

	sealed class call_graph_link {
		public MethodDef source { get; }
		public MethodDef target { get; }
		public int call_site_count { get; }
		public IReadOnlyList<uint> il_offsets { get; }
		public IReadOnlyList<string> op_codes { get; }
		public bool is_potential { get; }
		public IReadOnlyList<call_site_evidence> evidence { get; }

		public call_graph_link(MethodDef source, MethodDef target, int call_site_count,
			IReadOnlyList<uint> il_offsets, IReadOnlyList<string> op_codes, bool is_potential = false, IReadOnlyList<call_site_evidence>? evidence = null) {
			this.source = source;
			this.target = target;
			this.call_site_count = call_site_count;
			this.il_offsets = il_offsets;
			this.op_codes = op_codes;
			this.is_potential = is_potential;
			this.evidence = (evidence ?? il_offsets.Select(offset => new call_site_evidence(offset,
				string.Join(", ", op_codes), is_potential, is_potential ? "possible target inferred from IL" : "resolved IL operand or value flow")).ToArray())
				.Select(item => item with { target = target }).ToArray();
		}
	}

	sealed class call_graph_node {
		public MethodDef method { get; }
		public int layer { get; }
		public bool is_root { get; }
		public bool is_external { get; }
		public string expansion_hint { get; }
		public bool is_expanded { get; }

		public call_graph_node(MethodDef method, int layer, bool is_root, bool is_external, string expansion_hint = "", bool is_expanded = true) {
			this.method = method;
			this.layer = layer;
			this.is_root = is_root;
			this.is_external = is_external;
			this.expansion_hint = expansion_hint;
			this.is_expanded = is_expanded;
		}
	}

	sealed class call_graph_slice {
		public IReadOnlyList<call_graph_node> nodes { get; }
		public IReadOnlyList<call_graph_link> links { get; }
		public bool is_truncated { get; }
		public int unresolved_indirect_call_count { get; }
		public IReadOnlyList<graph_diagnostic> diagnostics { get; }
		public IReadOnlyList<call_site_info> call_sites { get; }
		public bool is_structural { get; }
		public bool can_continue_refinement { get; }
		public call_graph_direction direction { get; }
		readonly Dictionary<MethodDef, graph_diagnostic[]> diagnostics_by_method;
		public IReadOnlyList<graph_diagnostic> diagnostics_for(MethodDef method) => this.diagnostics_by_method.TryGetValue(method, out var values) ? values : Array.Empty<graph_diagnostic>();

		public call_graph_slice(IReadOnlyList<call_graph_node> nodes,
			IReadOnlyList<call_graph_link> links, bool is_truncated) : this(nodes, links, is_truncated, -1) { }
		public call_graph_slice(IReadOnlyList<call_graph_node> nodes,
			IReadOnlyList<call_graph_link> links, bool is_truncated, int unresolved_indirect_call_count, IReadOnlyList<graph_diagnostic>? diagnostics = null,
			IReadOnlyList<call_site_info>? call_sites = null, bool is_structural = false, bool can_continue_refinement = false, call_graph_direction direction = call_graph_direction.callees) {
			this.nodes = nodes;
			this.links = links;
			this.is_truncated = is_truncated;
			this.unresolved_indirect_call_count = unresolved_indirect_call_count;
			this.diagnostics = diagnostics ?? Array.Empty<graph_diagnostic>();
			this.call_sites = call_sites ?? call_site_info.from_links(links);
			this.is_structural = is_structural;
			this.can_continue_refinement = can_continue_refinement;
			this.direction = direction;
			this.diagnostics_by_method = this.diagnostics.GroupBy(item => item.method).ToDictionary(group => group.Key, group => group.ToArray());
		}
	}

	sealed class call_site_accumulator {
		public int count;
		public bool has_direct;
		public readonly HashSet<uint> offsets = new HashSet<uint>();
		public readonly HashSet<string> operations = new HashSet<string>(StringComparer.Ordinal);
		public readonly HashSet<call_site_evidence> evidence = new HashSet<call_site_evidence>();

		public void add(uint offset, string operation, bool is_potential, string? reason = null, call_evidence_kind? kind = null, bool contextual = false) {
			if (this.offsets.Add(offset))
				this.count++;
			this.operations.Add(operation);
			this.evidence.Add(new call_site_evidence(offset, operation, is_potential, reason ??
				(is_potential ? "possible runtime target or incomplete value flow" : "resolved IL operand or value flow"), classification: kind, is_contextual: contextual));
			if (!is_potential)
				this.has_direct = true;
		}

		public void add(call_graph_link link) {
			this.offsets.UnionWith(link.il_offsets);
			this.operations.UnionWith(link.op_codes);
			this.evidence.UnionWith(link.evidence);
			this.count = Math.Max(this.count, Math.Max(link.call_site_count, this.offsets.Count));
			if (!link.is_potential)
				this.has_direct = true;
		}
	}

	sealed class virtual_call_site {
		public MethodDef source { get; }
		public MethodDef declaration { get; }
		public uint offset { get; }
		public string operation { get; }

		public virtual_call_site(MethodDef source, MethodDef declaration, uint offset, string operation) {
			this.source = source;
			this.declaration = declaration;
			this.offset = offset;
			this.operation = operation;
		}
	}

	sealed class reflection_target {
		public MethodDef source { get; }
		public IReadOnlyList<string> type_names { get; }
		public IReadOnlyList<string> method_names { get; }
		public IReadOnlyList<MethodDef> exact_methods { get; }
		public uint offset { get; }
		public string operation { get; }

		public reflection_target(MethodDef source, IEnumerable<string> type_names, IEnumerable<string> method_names,
			IEnumerable<MethodDef> exact_methods, uint offset, string operation) {
			this.source = source;
			this.type_names = type_names.Distinct(StringComparer.Ordinal).ToArray();
			this.method_names = method_names.Distinct(StringComparer.Ordinal).ToArray();
			this.exact_methods = exact_methods.Distinct().ToArray();
			this.offset = offset;
			this.operation = operation;
		}
	}

	sealed class call_graph_index {
		readonly Dictionary<MethodDef, call_graph_link[]> outgoing;
		readonly Dictionary<MethodDef, call_graph_link[]> incoming;
		readonly HashSet<MethodDef> module_methods;
		readonly HashSet<ModuleDef> modules;
		readonly call_graph_link[] all_links;
		readonly virtual_call_site[] virtual_call_sites;
		readonly reflection_target[] reflection_targets;
		ILookup<string, virtual_call_site>? virtual_names;
		internal IEnumerable<virtual_call_site> virtual_sites_named(IEnumerable<string> names) {
			this.virtual_names ??= this.virtual_call_sites.ToLookup(site => site.declaration.Name.ToString(), StringComparer.Ordinal);
			return names.Distinct(StringComparer.Ordinal).SelectMany(name => this.virtual_names[name]);
		}
		readonly Dictionary<MethodDef, uint[]> unresolved_calli = new Dictionary<MethodDef, uint[]>();

		public ModuleDef? module { get; }
		public IReadOnlyCollection<ModuleDef> modules_value => this.modules;
		public int module_count => this.modules.Count;
		public int method_count => this.module_methods.Count;
		public int link_count { get; }
		public int skipped_method_count { get; }
		public bool is_reachable_index { get; }
		public IReadOnlyList<call_graph_link> all_links_value => this.all_links;
		internal IReadOnlyCollection<MethodDef> methods => this.module_methods;
		internal bool has_unresolved_references => this.diagnostics.Any(item => item.kind == graph_diagnostic_kind.unresolved_reference);
		IReadOnlyList<graph_diagnostic> diagnostics = Array.Empty<graph_diagnostic>();
		bool analysis_incomplete;
		bool can_continue_refinement;
		readonly Dictionary<MethodDef, call_graph_index> pending_callers = new();

		call_graph_index(ModuleDef? module, HashSet<ModuleDef> modules, HashSet<MethodDef> module_methods,
			Dictionary<MethodDef, call_graph_link[]> outgoing, Dictionary<MethodDef, call_graph_link[]> incoming,
			IEnumerable<virtual_call_site> virtual_call_sites, IEnumerable<reflection_target> reflection_targets,
			int skipped_method_count, bool is_reachable_index = false, IReadOnlyDictionary<MethodDef, uint[]>? unresolved_sites = null) {
			this.is_reachable_index = is_reachable_index;
			this.module = module;
			this.modules = modules;
			this.module_methods = module_methods;
			this.outgoing = outgoing;
			this.incoming = incoming;
			this.virtual_call_sites = virtual_call_sites.ToArray();
			this.reflection_targets = reflection_targets.ToArray();
			this.skipped_method_count = skipped_method_count;
			this.all_links = outgoing.Values.SelectMany(value => value).Distinct().ToArray();
			this.link_count = this.all_links.Length;
			if (unresolved_sites is not null) foreach (var pair in unresolved_sites) this.unresolved_calli[pair.Key] = pair.Value;
			foreach (var method in is_reachable_index || unresolved_sites is not null ? Enumerable.Empty<MethodDef>() : module_methods) {
				try {
					if (!method.HasBody) continue;
					var resolved = outgoing.TryGetValue(method, out var links)
						? new HashSet<uint>(links.Where(link => link.op_codes.Contains("calli")).SelectMany(link => link.il_offsets)) : new HashSet<uint>();
					var offsets = method.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Calli && !resolved.Contains(instruction.Offset))
						.Select(instruction => instruction.Offset).ToArray();
					if (offsets.Length != 0) this.unresolved_calli.Add(method, offsets);
				}
				catch (Exception) { }
			}
		}

		public static (call_graph_index index, call_graph_slice slice) build_reachable(MethodDef root,
			IReadOnlyList<ModuleDef> loaded_modules, int depth, int maximum_nodes, call_graph_filter_options filters,
			CancellationToken cancellation_token = default) => call_graph_index.build_reachable_progressive(root, loaded_modules, depth, maximum_nodes, filters, null, cancellation_token);
		public static (call_graph_index index, call_graph_slice slice) build_reachable_progressive(MethodDef root,
			IReadOnlyList<ModuleDef> loaded_modules, int depth, int maximum_nodes, call_graph_filter_options filters,
			IProgress<call_graph_slice>? progress, CancellationToken cancellation_token = default) {
			var workspace = new workspace_analysis(loaded_modules.Concat(new[] { root.Module }));
			return workspace.analyze(root, depth, maximum_nodes, filters, progress, cancellation_token);
		}
		internal static call_graph_index from_slice(MethodDef root, HashSet<ModuleDef> modules, call_graph_slice slice) =>
			new call_graph_index(root.Module, modules,
				new HashSet<MethodDef>(slice.nodes.Where(node => !node.is_external).Select(node => node.method)),
				slice.links.GroupBy(link => link.source).ToDictionary(group => group.Key, group => group.ToArray()),
				slice.links.GroupBy(link => link.target).ToDictionary(group => group.Key, group => group.ToArray()),
				Array.Empty<virtual_call_site>(), Array.Empty<reflection_target>(), 0, true);

		public static call_graph_index build(ModuleDef module, CancellationToken cancellation_token = default, workspace_analysis? workspace = null) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var methods = call_graph_index.enumerate_methods(module, cancellation_token).OrderBy(call_graph_index.safe_token).ToArray();
			return call_graph_index.build_scope(module, new HashSet<ModuleDef> { module }, methods, cancellation_token, workspace);
		}

		static call_graph_index build_scope(ModuleDef? module, HashSet<ModuleDef> modules, MethodDef[] methods, CancellationToken cancellation_token, workspace_analysis? workspace = null) {
			workspace ??= new workspace_analysis(modules);
			var call_sites = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			var virtual_sites = new List<virtual_call_site>();
			var notes = new List<graph_diagnostic>();
			int skipped = 0;
			foreach (var method in methods) {
				cancellation_token.ThrowIfCancellationRequested();
				try {
					var structure = workspace.call_structure(method, cancellation_token);
					if (structure.is_incomplete) { skipped++; notes.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.analysis_budget, "structural method instruction limit reached")); }
					foreach (var instruction in structure.calls) {
						if (instruction.Operand is not IMethod reference) continue;
						var target = workspace.resolve(reference);
						if (target is null) { notes.Add(new graph_diagnostic(method, instruction.Offset, graph_diagnostic_kind.unresolved_reference, reference.FullName)); continue; }
						bool polymorphic = instruction.OpCode.Code == Code.Callvirt && call_graph_index.is_polymorphic(target);
						call_graph_index.add_call_site(call_sites, method, target, instruction.Offset, instruction.OpCode.Name, true,
							polymorphic ? "structural virtual declaration" : "structural IL operand; reachability not refined", call_evidence_kind.il_operand);
						if (polymorphic) virtual_sites.Add(new virtual_call_site(method, target, instruction.Offset, "possible callvirt target"));
					}
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { skipped++; notes.Add(new graph_diagnostic(method, 0, graph_diagnostic_kind.invalid_il, error.GetType().Name)); }
			}
			var index = call_graph_index.create(module, modules, new HashSet<MethodDef>(methods), call_sites, virtual_sites, Array.Empty<reflection_target>(), skipped);
			index.diagnostics = notes.ToArray();
			return index;
		}
		public static call_graph_index combine(IEnumerable<call_graph_index> module_indexes, CancellationToken cancellation_token = default, workspace_analysis? workspace = null) {
			if (module_indexes is null)
				throw new ArgumentNullException(nameof(module_indexes));
			cancellation_token.ThrowIfCancellationRequested();
			var indexes = module_indexes.Where(index => index?.module is not null)
				.GroupBy(index => index.module!)
				.Select(group => group.Last())
				.ToArray();
			if (indexes.Length == 0)
				throw new ArgumentException("At least one single-module index is required.", nameof(module_indexes));
			cancellation_token.ThrowIfCancellationRequested();
			var modules = new HashSet<ModuleDef>(indexes.Select(index => index.module!));
			var methods = new HashSet<MethodDef>(indexes.SelectMany(index => index.module_methods));
			if (indexes.All(index => !index.is_reachable_index && index.reflection_targets.Length == 0)) {
				var links = indexes.SelectMany(index => index.all_links).ToArray();
				return new call_graph_index(null, modules, methods,
					links.GroupBy(link => link.source).ToDictionary(group => group.Key, group => group.ToArray()),
					links.GroupBy(link => link.target).ToDictionary(group => group.Key, group => group.ToArray()),
					indexes.SelectMany(index => index.virtual_call_sites), indexes.SelectMany(index => index.reflection_targets), indexes.Sum(index => index.skipped_method_count),
					unresolved_sites: indexes.SelectMany(index => index.unresolved_calli).ToDictionary(pair => pair.Key, pair => pair.Value)) {
					diagnostics = indexes.SelectMany(index => index.diagnostics).ToArray(),
				};
			}
			var sites = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			foreach (var link in indexes.SelectMany(index => index.all_links)) {
				cancellation_token.ThrowIfCancellationRequested();
				if (!sites.TryGetValue((source: link.source, target: link.target), out var accumulator)) sites[(source: link.source, target: link.target)] = accumulator = new call_site_accumulator();
				accumulator.add(link);
			}
			var virtual_sites = indexes.SelectMany(index => index.virtual_call_sites).ToArray();
			var reflection_sites = indexes.SelectMany(index => index.reflection_targets).ToArray();
			if (reflection_sites.Length != 0) (workspace?.resolver_value ?? new workspace_analysis(modules).resolver_value).add_edges(Array.Empty<virtual_call_site>(), reflection_sites, sites);
			var combined = call_graph_index.create(null, modules, methods, sites, virtual_sites, reflection_sites, indexes.Sum(index => index.skipped_method_count));
			combined.diagnostics = indexes.SelectMany(index => index.diagnostics).ToArray();
			return combined;
		}
		internal call_graph_index refine(workspace_analysis workspace, CancellationToken token, bool preserve_structural_calls = true, bool thorough_refinement = false, reachable_call_analysis? session = null) {
			var sites = new Dictionary<(MethodDef source, MethodDef target), call_site_accumulator>();
			foreach (var link in preserve_structural_calls ? this.all_links : Array.Empty<call_graph_link>()) {
				if (!sites.TryGetValue((source: link.source, target: link.target), out var accumulator)) sites[(source: link.source, target: link.target)] = accumulator = new call_site_accumulator();
				accumulator.add(link);
			}
			var virtual_sites = new List<virtual_call_site>(); var reflection_sites = new List<reflection_target>();
			var analysis = session ?? new reachable_call_analysis(this.modules, token, workspace, preserve_structural_calls);
			if (session is null && thorough_refinement) analysis.budget = new refinement_budget(TimeSpan.FromSeconds(30), long.MaxValue);
			int skipped = analysis.scan_index(this.module_methods.ToArray(), sites, virtual_sites, reflection_sites);
			var result = call_graph_index.create(null, this.modules, this.module_methods, sites, virtual_sites, reflection_sites, skipped);
			result.diagnostics = analysis.diagnostics_value.ToArray();
			result.analysis_incomplete = analysis.is_incomplete;
			result.can_continue_refinement = analysis.can_resume_index;
			result.unresolved_calli.Clear(); foreach (var pair in analysis.unresolved_indirect_sites) result.unresolved_calli[pair.Key] = pair.Value;
			return result;
		}

		static call_graph_index create(ModuleDef? module, HashSet<ModuleDef> modules, HashSet<MethodDef> methods,
			Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> call_sites,
			IEnumerable<virtual_call_site> virtual_sites, IEnumerable<reflection_target> reflection_sites, int skipped) {
			var links = call_sites.Select(pair => new call_graph_link(pair.Key.source, pair.Key.target,
				pair.Value.count, pair.Value.offsets.OrderBy(offset => offset).ToArray(),
				pair.Value.operations.OrderBy(value => value, StringComparer.Ordinal).ToArray(), !pair.Value.has_direct,
				pair.Value.evidence.OrderBy(item => item.offset).ThenBy(item => item.operation, StringComparer.Ordinal).ToArray()))
				.OrderBy(link => call_graph_index.safe_token(link.source))
				.ThenBy(link => call_graph_index.safe_token(link.target))
				.ToArray();
			return new call_graph_index(module, modules, methods,
				links.GroupBy(link => link.source).ToDictionary(group => group.Key, group => group.ToArray()),
				links.GroupBy(link => link.target).ToDictionary(group => group.Key, group => group.ToArray()),
				virtual_sites, reflection_sites, skipped);
		}

		internal static void add_call_site(Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> call_sites,
			MethodDef source, MethodDef target, uint offset, string operation, bool is_potential, string? reason = null, call_evidence_kind? kind = null, bool contextual = false) {
			var key = (source: source, target: target);
			if (!call_sites.TryGetValue(key, out var accumulator))
				call_sites.Add(key, accumulator = new call_site_accumulator());
			accumulator.add(offset, operation, is_potential, reason, kind, contextual);
		}

		internal static MethodDef? resolve(IMethod reference, Dictionary<IMethod, MethodDef?> cache) {
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

		internal static string safe_type_name(ITypeDefOrRef? type) {
			try { return type?.FullName ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		internal static string safe_method_name(MethodDef method) {
			try { return method.Name?.ToString() ?? string.Empty; }
			catch (Exception) { return string.Empty; }
		}

		internal static uint safe_token(MethodDef method) {
			try { return method.MDToken.Raw; }
			catch (Exception) { return uint.MaxValue; }
		}

		static bool is_polymorphic(MethodDef method) {
			try { return method.IsVirtual || method.DeclaringType?.IsInterface == true; }
			catch (Exception) { return false; }
		}

		static bool is_direct_call_instruction(Code code) =>
			code == Code.Call || code == Code.Callvirt || code == Code.Newobj || code == Code.Jmp;

		static IEnumerable<MethodDef> enumerate_methods(ModuleDef module, CancellationToken cancellation_token) {
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

		public bool contains(MethodDef method) => this.module_methods.Contains(method);
		public IReadOnlyList<uint> unresolved_calli_sites(MethodDef method) =>
			this.unresolved_calli.TryGetValue(method, out var offsets) ? offsets : Array.Empty<uint>();
		public bool contains_module(ModuleDef module) => this.modules.Contains(module);
		public IReadOnlyList<call_graph_link> outgoing_value(MethodDef method) =>
			this.outgoing.TryGetValue(method, out var links) ? links : Array.Empty<call_graph_link>();
		public IReadOnlyList<call_graph_link> incoming_value(MethodDef method) =>
			this.incoming.TryGetValue(method, out var links) ? links : Array.Empty<call_graph_link>();
		internal call_graph_index merge_links(IEnumerable<call_graph_link> additions, CancellationToken token) {
			var links = this.all_links.ToDictionary(link => (link.source, link.target)); bool changed = false;
			foreach (var link in additions) {
				token.ThrowIfCancellationRequested();
				var key = (link.source, link.target);
				if (!links.TryGetValue(key, out var existing)) { links[key] = link; changed = true; continue; }
				var existing_evidence = existing.evidence.ToHashSet(); var existing_offsets = existing.il_offsets.ToHashSet();
				if (link.evidence.All(existing_evidence.Contains) && link.il_offsets.All(existing_offsets.Contains)) continue;
				var accumulator = new call_site_accumulator(); accumulator.add(existing); accumulator.add(link);
				links[key] = workspace_analysis.link(key.source, key.target, accumulator); changed = true;
			}
			if (!changed) return this;
			return new call_graph_index(this.module, this.modules, this.module_methods,
				links.Values.GroupBy(link => link.source).ToDictionary(group => group.Key, group => group.ToArray()),
				links.Values.GroupBy(link => link.target).ToDictionary(group => group.Key, group => group.ToArray()),
				this.virtual_call_sites, this.reflection_targets, this.skipped_method_count, this.is_reachable_index, this.unresolved_calli) {
				diagnostics = this.diagnostics, analysis_incomplete = this.analysis_incomplete, can_continue_refinement = this.can_continue_refinement,
			};
		}
		internal call_graph_index with_pending_callers(MethodDef root) {
			if (this.pending_callers.TryGetValue(root, out var cached)) return cached;
			var result = new call_graph_index(this.module, this.modules, this.module_methods, this.outgoing, this.incoming,
			this.virtual_call_sites, this.reflection_targets, this.skipped_method_count, this.is_reachable_index, this.unresolved_calli) {
			diagnostics = this.diagnostics.Concat(new[] { new graph_diagnostic(root, 0, graph_diagnostic_kind.analysis_budget, ui_text.pick("Additional argument-dependent callers remain under refinement; known IL links are retained", "Уточнение вызывающих, зависящих от аргументов, не завершено; найденные IL-связи сохранены")) }).ToArray(),
			analysis_incomplete = true, can_continue_refinement = true,
		};
			if (this.pending_callers.Count >= 32) this.pending_callers.Remove(this.pending_callers.Keys.First());
			this.pending_callers[root] = result; return result;
		}

		public call_graph_slice select(MethodDef root, call_graph_direction direction, int depth, int maximum_nodes,
			call_graph_filter_options? filter_options = null, CancellationToken cancellation_token = default, workspace_analysis? workspace = null, bool structural = false, call_graph_slice? forward_slice = null) {
			cancellation_token.ThrowIfCancellationRequested();
			workspace ??= new workspace_analysis(this.modules);
			if (direction == call_graph_direction.callees)
				return structural ? workspace.structural_slice(root, depth, maximum_nodes, filter_options ?? call_graph_filter_options.none, cancellation_token) :
					workspace.analyze(root, depth, maximum_nodes, filter_options ?? call_graph_filter_options.none, null, cancellation_token).slice;
			maximum_nodes = Math.Max(2, maximum_nodes);
			depth = Math.Max(1, Math.Min(depth, maximum_nodes - 1));
			var filter = filter_options ?? call_graph_filter_options.none;
			string filter_key = string.Join(":", filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors, filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count, filter.only_current_assembly, filter.preserve_structural_calls, filter.thorough_refinement);
			var request = new graph_selection_key(this, root, direction, depth, maximum_nodes, filter_key, structural, forward_slice);
			bool cache_selection = direction == call_graph_direction.callers || structural || forward_slice is not null;
			if (cache_selection && workspace.selections.try_get(request, workspace.relation_revision, out var cached)) return cached;
			var forward = direction == call_graph_direction.both
				? forward_slice ?? (structural ? workspace.structural_slice(root, depth, maximum_nodes, filter, cancellation_token) :
					workspace.analyze(root, depth, maximum_nodes, filter, null, cancellation_token).slice) : null;
			var index = workspace.include_discoveries(this, cancellation_token);
			var forward_nodes = forward?.nodes.ToDictionary(node => node.method);
			var ordered_forward = forward?.nodes.OrderBy(node => node.layer).ThenBy(node => call_graph_index.safe_token(node.method)).ToArray() ?? Array.Empty<call_graph_node>();
			var layers = new Dictionary<MethodDef, int> { [root] = 0 };
			var visible_filter = new call_graph_filter_options(filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors, filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count);
			bool has_incoming = workspace.incoming_links(index, root, cancellation_token).Any(link => visible_filter.includes(root, link, link.source) && link.source != root);
			int forward_limit = has_incoming ? 1 + (maximum_nodes - 1) / 2 : maximum_nodes;
			foreach (var node in ordered_forward) { if (layers.Count >= forward_limit) break; layers.TryAdd(node.method, node.layer); }
			bool truncated = forward?.is_truncated == true || this.analysis_incomplete;
			var view_notes = new List<graph_diagnostic>();
			if (direction != call_graph_direction.callees)
				call_graph_index.traverse(root, depth, maximum_nodes, -1, method => workspace.incoming_links(index, method, cancellation_token), link => link.source, filter, layers, ref truncated, cancellation_token, view_notes);
			foreach (var node in ordered_forward) { if (layers.Count >= maximum_nodes) break; layers.TryAdd(node.method, node.layer); }
			foreach (var method in layers.Keys) workspace.incoming_links(index, method, cancellation_token);
			index = workspace.include_discoveries(index, cancellation_token);
			if (ordered_forward.Any(node => !layers.ContainsKey(node.method))) { truncated = true; view_notes.Add(new graph_diagnostic(root, 0, graph_diagnostic_kind.node_limit, ui_text.pick("Outgoing targets outside the shared card limit — expand here", "Вызываемые вне общего лимита карточек — раскрыть здесь"))); }
			var notes_by_method = view_notes.ToLookup(note => note.method);
			var visible = new HashSet<MethodDef>(layers.Keys);
			var nodes = layers.Select(pair => new call_graph_node(pair.Key, pair.Value, pair.Key == root,
				!this.module_methods.Contains(pair.Key), string.Join("; ", new[] { forward_nodes?.GetValueOrDefault(pair.Key)?.expansion_hint ?? "" }.Concat(notes_by_method[pair.Key].Select(note => note.detail)).Where(text => text.Length != 0).Distinct()), forward_nodes?.GetValueOrDefault(pair.Key)?.is_expanded ??
					(Math.Abs(pair.Value) < depth && this.modules.Contains(pair.Key.Module) && (!filter.only_current_assembly || ReferenceEquals(root.Module, pair.Key.Module) || root.Module.Assembly is not null && ReferenceEquals(root.Module.Assembly, pair.Key.Module.Assembly)))))
				.OrderBy(node => node.layer)
				.ThenBy(node => call_graph_index.safe_token(node.method))
				.ToArray();
			var links = visible.SelectMany(method => index.outgoing.TryGetValue(method, out var method_links)
					? method_links : Array.Empty<call_graph_link>())
				.Where(link => visible.Contains(link.target) && link.call_site_count >= filter.minimum_call_site_count)
				.Concat(forward?.links ?? Array.Empty<call_graph_link>())
				.Where(link => visible.Contains(link.source) && visible.Contains(link.target))
				.GroupBy(link => (link.source, link.target)).Select(group => { var accumulator = new call_site_accumulator(); foreach (var link in group) accumulator.add(link); return workspace_analysis.link(group.Key.source, group.Key.target, accumulator); })
				.OrderBy(link => layers[link.source])
				.ThenBy(link => call_graph_index.safe_token(link.source))
				.ThenBy(link => call_graph_index.safe_token(link.target))
				.ToArray();
			var slice = new call_graph_slice(nodes, links, truncated, (forward?.unresolved_indirect_call_count ?? 0) +
				layers.Where(pair => forward is null || pair.Value < 0).Sum(pair => this.unresolved_calli_sites(pair.Key).Count),
				(forward?.diagnostics ?? Array.Empty<graph_diagnostic>()).Concat(this.diagnostics.Where(item => visible.Contains(item.method) && (forward is null || layers[item.method] < 0))).Concat(view_notes).ToArray());
			var result = new call_graph_slice(nodes, links, truncated, slice.unresolved_indirect_call_count, slice.diagnostics, workspace.describe_sites(slice, cancellation_token), structural, this.can_continue_refinement || forward?.can_continue_refinement == true, direction);
			if (cache_selection) workspace.selections.store(request, workspace.relation_revision, result);
			return result;
		}

		static void traverse(MethodDef root, int maximum_depth, int maximum_nodes, int layer_sign,
			Func<MethodDef, IReadOnlyList<call_graph_link>> adjacency, Func<call_graph_link, MethodDef> select_target,
			call_graph_filter_options filter, Dictionary<MethodDef, int> layers, ref bool truncated, CancellationToken cancellation_token, ICollection<graph_diagnostic> notes) {
			var visible_filter = new call_graph_filter_options(filter.hide_system_methods, filter.hide_accessors, filter.hide_constructors, filter.only_current_namespace, filter.only_current_type, filter.require_method_body, filter.minimum_call_site_count);
			var visited = new HashSet<MethodDef> { root };
			var emitted = notes.Select(note => (note.method, note.kind)).ToHashSet();
			var queue = new Queue<(MethodDef method, int depth)>();
			queue.Enqueue((method: root, 0));
			while (queue.Count != 0) {
				cancellation_token.ThrowIfCancellationRequested();
				var current = queue.Dequeue();
				var links = adjacency(current.method);
				if (links.Count == 0) continue;
				if (filter.only_current_assembly && current.method != root && !ReferenceEquals(root.Module, current.method.Module) && (root.Module.Assembly is null || !ReferenceEquals(root.Module.Assembly, current.method.Module.Assembly))) {
					truncated = true; add_note(current.method, graph_diagnostic_kind.assembly_boundary, ui_text.pick("External caller — expand here to continue", "Внешний вызывающий — раскрыть здесь для продолжения")); continue;
				}
				if (current.depth >= maximum_depth) {
					if (links.Any(link => !layers.ContainsKey(select_target(link)) && visible_filter.includes(root, link, select_target(link)))) {
						truncated = true; add_note(current.method, graph_diagnostic_kind.view_depth, ui_text.pick("Incoming view depth boundary — Explore from here", "Граница глубины вызывающих — исследовать отсюда"));
					}
					continue;
				}
				foreach (var link in links) {
					var next = select_target(link);
					if (!visible_filter.includes(root, link, next)) { add_note(current.method, graph_diagnostic_kind.filtered_target, ui_text.pick("Incoming targets hidden by filters", "Вызывающие скрыты фильтрами")); continue; }
					if (!visited.Add(next)) continue;
					if (!layers.ContainsKey(next) && layers.Count >= maximum_nodes) {
						truncated = true;
						add_note(current.method, graph_diagnostic_kind.node_limit, ui_text.pick("Incoming targets outside card limit", "Вызывающие вне лимита карточек"));
						continue;
					}
					int next_depth = current.depth + 1;
					if (!layers.ContainsKey(next)) layers[next] = next_depth * layer_sign;
					queue.Enqueue((method: next, depth: next_depth));
				}
			}
			void add_note(MethodDef method, graph_diagnostic_kind kind, string detail) { if (emitted.Add((method, kind))) notes.Add(new graph_diagnostic(method, 0, kind, detail)); }
		}

		public static string display_name(MethodDef method) {
			try {
				string declaring_type = method.DeclaringType?.FullName ?? "<Module>";
				return declaring_type + "::" + (method.Name?.ToString() ?? $"method_{call_graph_index.safe_token(method):X8}");
			}
			catch (Exception) { return $"method_0x{call_graph_index.safe_token(method):X8}"; }
		}

		public static string signature(MethodDef method) {
			try {
				string parameters = method.Parameters.Count == 0 ? string.Empty :
					string.Join(", ", method.Parameters.Where(parameter => !parameter.IsHiddenThisParameter)
						.Select(parameter => parameter.Type?.TypeName ?? "?"));
				return $"{call_graph_index.display_name(method)}({parameters})";
			}
			catch (Exception) { return call_graph_index.display_name(method) + "(?)"; }
		}
	}

	sealed class call_graph_index_cache {
		readonly object sync = new object();
		ConcurrentDictionary<MethodDef, method_structure> structures = new ConcurrentDictionary<MethodDef, method_structure>();
		ConcurrentDictionary<MethodDef, method_structure> call_structures = new();
		ConcurrentDictionary<MethodDef, method_effects> effects = new ConcurrentDictionary<MethodDef, method_effects>();
		workspace_analysis? workspace;
		HashSet<ModuleDef> workspace_modules = new HashSet<ModuleDef>();
		readonly Dictionary<ModuleDef, call_graph_index> entries = new Dictionary<ModuleDef, call_graph_index>();
		call_graph_index[] combined_sources = Array.Empty<call_graph_index>();
		call_graph_index? combined_index;
		public workspace_analysis get_workspace(IEnumerable<ModuleDef> modules) {
			var loaded = new HashSet<ModuleDef>(modules);
			lock (this.sync) {
				if (!this.workspace_modules.SetEquals(loaded)) {
					var removed = new HashSet<ModuleDef>(this.workspace_modules.Except(loaded));
					var added_assemblies = new HashSet<string>(loaded.Except(this.workspace_modules).Select(module => module.Assembly?.FullName ?? ""), StringComparer.OrdinalIgnoreCase);
					foreach (var entry in this.entries.ToArray()) if (!loaded.Contains(entry.Key) || entry.Value.has_unresolved_references || entry.Value.all_links_value.Any(link => removed.Contains(link.target.Module) ||
						!this.workspace_modules.Contains(link.target.Module) && added_assemblies.Contains(link.target.Module.Assembly?.FullName ?? ""))) this.entries.Remove(entry.Key);
					this.invalidate_workspace(removed);
					this.workspace_modules = loaded;
				}
				if (this.workspace is null || !this.workspace.modules.SetEquals(loaded)) {
					this.effects = new ConcurrentDictionary<MethodDef, method_effects>(this.effects.Where(pair => !pair.Value.has_unknown_effects && pair.Value.dependencies.All(loaded.Contains)));
					this.workspace = new workspace_analysis(loaded, this.structures, this.effects, this.call_structures);
				}
				return this.workspace;
			}
		}
		void invalidate_workspace(HashSet<ModuleDef> changed) {
			this.call_structures = new ConcurrentDictionary<MethodDef, method_structure>(this.call_structures.Where(pair => !changed.Contains(pair.Key.Module)));
			this.structures = new ConcurrentDictionary<MethodDef, method_structure>(this.structures.Where(pair => !changed.Contains(pair.Key.Module) &&
				!pair.Value.reads.Concat(pair.Value.writes).Any(field => changed.Contains(field.Module))));
			this.effects = new ConcurrentDictionary<MethodDef, method_effects>(this.effects.Where(pair => !pair.Value.has_unknown_effects && !pair.Value.dependencies.Any(changed.Contains)));
			this.workspace = null; this.reset_combined();
		}

		public int count {
			get { lock (this.sync) return this.entries.Count; }
		}

		public bool try_get_value(ModuleDef module, out call_graph_index index) {
			lock (this.sync)
				return this.entries.TryGetValue(module, out index!);
		}

		public bool store(call_graph_index index, workspace_analysis? expected_workspace = null) {
			if (index.module is not ModuleDef module)
				throw new ArgumentException("Only single-module indexes can be cached.", nameof(index));
			lock (this.sync) {
				if (expected_workspace is not null && !ReferenceEquals(this.workspace, expected_workspace)) return false;
				this.entries[module] = index;
				this.reset_combined();
				return true;
			}
		}
		public call_graph_index get_or_build_combined(IReadOnlyList<ModuleDef> modules, workspace_analysis expected_workspace, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			var sources = new List<call_graph_index>();
			foreach (var module in modules) {
				token.ThrowIfCancellationRequested();
				if (!this.try_get_value(module, out var index)) {
					index = call_graph_index.build(module, token, expected_workspace);
					token.ThrowIfCancellationRequested(); this.store(index, expected_workspace);
				}
				sources.Add(index);
			}
			if (!this.try_get_combined(sources, out var combined)) {
				combined = call_graph_index.combine(sources, token, expected_workspace);
				token.ThrowIfCancellationRequested(); this.store_combined(sources, combined, expected_workspace);
			}
			token.ThrowIfCancellationRequested(); return combined;
		}

		public bool try_get_combined(IReadOnlyList<call_graph_index> sources, out call_graph_index index) {
			lock (this.sync) {
				if (this.combined_index is not null && this.combined_sources.Length == sources.Count) {
					bool matches = true;
					for (int i = 0; i < this.combined_sources.Length; i++) {
						if (ReferenceEquals(this.combined_sources[i], sources[i]))
							continue;
						matches = false;
						break;
					}
					if (matches) {
						index = this.combined_index;
						return true;
					}
				}
				index = null!;
				return false;
			}
		}

		public bool store_combined(IReadOnlyList<call_graph_index> sources, call_graph_index index, workspace_analysis? expected_workspace = null) {
			lock (this.sync) {
				if (expected_workspace is not null && !ReferenceEquals(this.workspace, expected_workspace)) return false;
				this.combined_sources = sources.ToArray();
				this.combined_index = index;
				return true;
			}
		}

		public bool invalidate(ModuleDef module) {
			return this.invalidate(new[] { module }) != 0;
		}

		public int invalidate(IEnumerable<ModuleDef> modules) {
			lock (this.sync) {
				var changed = new HashSet<ModuleDef>(modules);
				int count = 0;
				foreach (var module in this.entries.Keys.Where(module => changed.Contains(module) || this.entries[module].has_unresolved_references || this.entries[module].all_links_value.Any(link => changed.Contains(link.target.Module))).ToArray())
					if (this.entries.Remove(module))
						count++;
				this.invalidate_workspace(changed);
				return count;
			}
		}

		public int remove_missing(ISet<ModuleDef> loaded_modules) {
			lock (this.sync) {
				var removed_modules = new HashSet<ModuleDef>(this.workspace_modules.Concat(this.entries.Keys).Concat(this.structures.Keys.Select(method => method.Module)).Where(module => !loaded_modules.Contains(module)));
				int count = 0;
				foreach (var module in this.entries.Keys.Where(module => !loaded_modules.Contains(module) || this.entries[module].all_links_value.Any(link => removed_modules.Contains(link.target.Module))).ToArray())
					if (this.entries.Remove(module))
						count++;
				if (count != 0 || removed_modules.Count != 0) this.invalidate_workspace(removed_modules);
				return count;
			}
		}

		public void clear() {
			lock (this.sync) {
				this.entries.Clear();
				this.workspace_modules.Clear();
				this.structures = new ConcurrentDictionary<MethodDef, method_structure>(); this.effects = new ConcurrentDictionary<MethodDef, method_effects>(); this.workspace = null;
				this.reset_combined();
			}
		}

		void reset_combined() {
			this.combined_sources = Array.Empty<call_graph_index>();
			this.combined_index = null;
		}
	}
}
