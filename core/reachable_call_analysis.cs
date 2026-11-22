using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class reachable_call_analysis {
		sealed class call {
			public readonly Instruction instruction;
			public readonly MethodDef target;
			public symbolic_value[] arguments;
			public readonly bool potential;
			public readonly string operation;
			public readonly string? reason;
			public readonly call_evidence_kind kind;
			public symbolic_state memory;
			public call(Instruction instruction, MethodDef target, symbolic_value[] arguments, symbolic_state memory, bool potential = false, string? operation = null, string? reason = null, call_evidence_kind? kind = null) {
				this.instruction = instruction; this.target = target; this.arguments = arguments; this.potential = potential || memory.exceptional_path || memory.conservative_context;
				this.operation = operation ?? instruction.OpCode.Name;
				this.reason = reason ?? (memory.exceptional_path ? "possible call on an exceptional control-flow path" : memory.conservative_context ? "possible call from a conservative context" : null);
				this.kind = kind ?? (this.potential ? call_evidence_kind.possible_target : call_evidence_kind.resolved_target);
				this.memory = new symbolic_state(); this.memory.import_memory(memory);
			}
		}
		static readonly symbolic_state empty_memory = new symbolic_state();
		static void release_context(work work) {
			work.flow = null; work.arguments = Array.Empty<symbolic_value>(); work.memory = reachable_call_analysis.empty_memory;
			if (work.trace is not null) foreach (var call in work.trace.calls) { call.arguments = Array.Empty<symbolic_value>(); call.memory = reachable_call_analysis.empty_memory; }
		}
		sealed class trace {
			public bool incomplete;
			public readonly List<call> calls = new List<call>();
			public readonly HashSet<uint> indirect_sites = new HashSet<uint>();
			public readonly HashSet<uint> resolved_sites = new HashSet<uint>();
			public readonly HashSet<uint> unresolved_sites = new HashSet<uint>();
			public readonly HashSet<uint> reached = new HashSet<uint>();
		}
		sealed class work {
			public required MethodDef method;
			public required symbolic_value[] arguments;
			public required symbolic_state memory;
			public int depth;
			public bool contextual;
			public bool initialize_arguments;
			public trace? trace;
			public method_flow_continuation? flow;
			public readonly Dictionary<(MethodDef, uint, graph_diagnostic_kind), graph_diagnostic> notes = new();
		}
		readonly Queue<work> frontier = new();
		readonly Dictionary<string, work> selected_contexts = new();
		readonly Dictionary<MethodDef, int> selected_layers = new();
		work? walking;
		MethodDef? selected_root;
		bool view_truncated, helper_interrupted;
		int refinement_pass;
		internal bool can_resume => this.frontier.Count != 0;
		readonly List<work> index_work = new();
		readonly Dictionary<MethodDef, HashSet<string>> index_contexts = new();
		int index_position;
		internal bool can_resume_index => this.index_position < this.index_work.Count;
		internal int completed_context_count { get; private set; }
		internal void renew(CancellationToken token) {
			this.cancellation_token = token; this.evaluator.set_cancellation_token(token); this.runner.set_cancellation_token(token);
			this.refinement_pass = Math.Min(8, this.refinement_pass + 1);
			this.budget = new refinement_budget(TimeSpan.FromSeconds(Math.Min(30, 3 * (1 << this.refinement_pass))), Math.Min(1024L, 256L << this.refinement_pass) * 1024 * 1024);
			this.remaining_steps = 300000 * (1 << this.refinement_pass); this.remaining_helper_steps = 750000 * (1 << this.refinement_pass);
			this.incomplete = false;
		}

		readonly HashSet<ModuleDef> modules;
		readonly workspace_analysis workspace;
		bool preserve_structural_calls;
		readonly Dictionary<IMethod, MethodDef?> resolved = new Dictionary<IMethod, MethodDef?>();
		readonly pointer_flow_catalog storage = new pointer_flow_catalog();
		readonly HashSet<TypeDef> initialized = new HashSet<TypeDef>();
		readonly Dictionary<TypeDef, MethodDef?> type_initializers = new Dictionary<TypeDef, MethodDef?>();
		MethodDef? type_initializer(TypeDef type) {
			if (!this.type_initializers.TryGetValue(type, out var method)) this.type_initializers[type] = method = type.FindStaticConstructor();
			return method;
		}
		readonly context_call_evaluator evaluator;
		readonly method_flow_runner runner;
		readonly invocation_model invocations;
		CancellationToken cancellation_token;
		readonly Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> call_sites = new Dictionary<(MethodDef, MethodDef), call_site_accumulator>();
		readonly Dictionary<(MethodDef, uint, graph_diagnostic_kind), graph_diagnostic> diagnostics = new Dictionary<(MethodDef, uint, graph_diagnostic_kind), graph_diagnostic>();
		readonly Dictionary<(MethodDef, uint, graph_diagnostic_kind), graph_diagnostic> transient_notes = new();
		bool transient_report;
		readonly List<reflection_target> reflection_sites = new List<reflection_target>();
		readonly List<virtual_call_site> virtual_sites = new List<virtual_call_site>();
		internal IReadOnlyList<graph_diagnostic> diagnostics_value => this.diagnostics.Values.Concat(this.index_work.SelectMany(work => work.notes.Values)).Distinct().ToArray();
		void report(MethodDef method, uint offset, graph_diagnostic_kind kind, string detail, MethodDef? target = null) {
			var note = new graph_diagnostic(method, offset, kind, detail, target);
			if (this.transient_report) this.transient_notes[(method, offset, kind)] = note;
			else if (this.walking is not null) this.walking.notes[(method, offset, kind)] = note;
			else this.diagnostics[(method, offset, kind)] = note;
		}
		readonly Dictionary<MethodDef, HashSet<uint>> indirect_sites = new Dictionary<MethodDef, HashSet<uint>>();
		readonly Dictionary<MethodDef, HashSet<uint>> resolved_sites = new Dictionary<MethodDef, HashSet<uint>>();
		readonly HashSet<(MethodDef method, uint offset)> unknown_sites = new HashSet<(MethodDef, uint)>();
		internal IReadOnlyDictionary<MethodDef, uint[]> unresolved_indirect_sites => this.unknown_sites.GroupBy(site => site.method)
			.ToDictionary(group => group.Key, group => group.Select(site => site.offset).OrderBy(offset => offset).ToArray());
		void record_indirect_sites(MethodDef method, trace trace) {
			if (!this.indirect_sites.TryGetValue(method, out var indirect)) this.indirect_sites[method] = indirect = new HashSet<uint>();
			if (!this.resolved_sites.TryGetValue(method, out var known)) this.resolved_sites[method] = known = new HashSet<uint>();
			indirect.UnionWith(trace.indirect_sites); known.UnionWith(trace.resolved_sites);
			foreach (uint offset in trace.unresolved_sites) this.unknown_sites.Add((method: method, offset: offset));
		}
		readonly Dictionary<MethodDef, MethodDef[]> virtual_targets = new Dictionary<MethodDef, MethodDef[]>();
		readonly HashSet<IMethod> fallback_attempted = new HashSet<IMethod>();
		Dictionary<string, TypeDef[]>? loaded_types;
		int remaining_steps = 300000;
		int remaining_helper_steps = 750000;
		int method_steps, method_budget;
		int helper_steps, helper_budget;
		int remaining_structural_instructions = 2000000;
		readonly Dictionary<MethodDef, Instruction[]> structural_calls = new Dictionary<MethodDef, Instruction[]>();
		readonly Dictionary<MethodDef, HashSet<Instruction>> continuation_calls = new Dictionary<MethodDef, HashSet<Instruction>>();
		readonly Dictionary<MethodDef, symbolic_value[]> unknown_arguments = new Dictionary<MethodDef, symbolic_value[]>();
		bool incomplete;
		internal bool is_incomplete => this.incomplete || this.diagnostics_value.Any(note => note.kind is graph_diagnostic_kind.analysis_budget or graph_diagnostic_kind.unsupported_instruction or graph_diagnostic_kind.invalid_il);
		internal refinement_budget budget { get; set; } = new refinement_budget();
		bool initial_state_known;
		Dictionary<FieldDef, List<(MethodDef method, int position)>>? field_writers;
		readonly HashSet<FieldDef> inferred_fields = new HashSet<FieldDef>();

		public reachable_call_analysis(IEnumerable<ModuleDef> modules, CancellationToken cancellation_token, workspace_analysis? workspace = null, bool preserve_structural_calls = true) {
			this.preserve_structural_calls = preserve_structural_calls;
			this.modules = new HashSet<ModuleDef>(modules);
			this.workspace = workspace ?? new workspace_analysis(this.modules);
			this.cancellation_token = cancellation_token;
			this.evaluator = new context_call_evaluator(this.resolved, cancellation_token, this.field_value, this.storage.element_value, this.resolve, this.storage);
			this.runner = new method_flow_runner(cancellation_token);
			this.invocations = new invocation_model(this.modules, this.resolve, (method, arguments, state) => this.evaluator.evaluate(method, arguments, state), this.workspace.types_value);
			this.evaluator.summaries = this.workspace.summaries_value;
			this.evaluator.invocations = this.invocations;
			this.evaluator.consume_step = this.consume_helper_step;
			this.evaluator.request_budget = requested => this.helper_budget = Math.Max(this.helper_budget, requested);
			this.evaluator.can_analyze = method => this.modules.Contains(method.Module);
			this.evaluator.before_instruction = (method, instruction, state) => this.initialize_for_instruction(method, instruction, state, null);
			this.evaluator.before_method = (method, state) => this.initialize_for_method(method, state, null, null);
		}
		bool consume_step() => --this.remaining_steps >= 0 && ++this.method_steps <= this.method_budget && this.budget.consume_step();
		bool consume_helper_step() { bool allowed = --this.remaining_helper_steps >= 0 && ++this.helper_steps <= this.helper_budget && this.budget.consume_step(); this.helper_interrupted |= !allowed; return allowed; }
		internal int scan_index(IReadOnlyList<MethodDef> methods, Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> result,
			List<virtual_call_site> virtual_calls, List<reflection_target> reflection_calls) {
			this.remaining_steps = int.MaxValue;
			this.remaining_helper_steps = int.MaxValue;
			if (this.index_work.Count == 0) foreach (var method in methods.Where(method => method.HasBody).OrderBy(method => method.Body.Instructions.Count).ThenBy(call_graph_index.safe_token)) {
				this.index_work.Add(new work { method = method, arguments = Array.Empty<symbolic_value>(), memory = new symbolic_state(), initialize_arguments = true });
			}
			int skipped = 0;
			while (this.index_position < this.index_work.Count) {
				this.cancellation_token.ThrowIfCancellationRequested();
				if (!this.budget.can_continue()) break;
				var current = this.index_work[this.index_position]; var method = current.method;
				if (current.initialize_arguments) {
					current.arguments = this.unknown_arguments_value(method); current.initialize_arguments = false;
					if (!this.index_contexts.TryGetValue(method, out var contexts)) this.index_contexts[method] = contexts = new HashSet<string>();
					contexts.Add(this.evaluator.context_key(method, current.arguments, current.memory));
				}
				global::dnSpy.CallGraph.Core.reachable_call_analysis.trace trace;
				this.walking = current;
				foreach (var key in current.notes.Keys.Where(key => key.Item3 == graph_diagnostic_kind.analysis_budget).ToArray()) current.notes.Remove(key);
				try {
					trace = this.can_read_structurally(method) ? this.read_structural_trace(method) : this.walk(method, current.arguments, current.memory);
				}
				catch (flow_suspended_exception) { this.helper_interrupted = true; trace = current.trace ?? new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace(); }
				catch (OperationCanceledException) { current.flow = null; current.trace = null; current.notes.Clear(); this.evaluator.clear_cache(true); throw; }
				catch (Exception error) {
					skipped++; this.incomplete = true; this.report(method, 0, graph_diagnostic_kind.unresolved_reference, error.GetType().Name + ": " + error.Message);
					trace = new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace { incomplete = true }; this.retain_structural_calls(method, trace);
				}
				finally { this.walking = null; }
				current.trace = trace;
				if (current.flow?.is_paused == true || this.helper_interrupted) {
					if (this.helper_interrupted && current.flow?.is_paused != true) { current.flow = null; current.trace = null; current.notes.Clear(); }
					if (this.index_work.Count - this.index_position > 1) { this.index_work.RemoveAt(this.index_position); this.index_work.Add(current); }
					break;
				}
				int insertion = this.index_position + 1;
				foreach (var call in trace.calls) {
					if (!this.modules.Contains(call.target.Module) || !call.target.HasBody || call.arguments.All(value => value.is_unknown) && call.memory.memory.IsEmpty) continue;
					if (!this.index_contexts.TryGetValue(call.target, out var contexts)) this.index_contexts[call.target] = contexts = new HashSet<string>();
					string context = this.evaluator.context_key(call.target, call.arguments, call.memory);
					if (contexts.Contains(context)) continue;
					if (contexts.Count >= 16 || this.index_work.Count >= methods.Count + 8192) {
						this.report(call.target, 0, graph_diagnostic_kind.runtime_selection, "Incoming context widening; additional argument-dependent targets remain unknown"); continue;
					}
					contexts.Add(context);
					this.index_work.Insert(insertion++, new work { method = call.target, arguments = call.arguments, memory = call.memory, contextual = true });
				}
				reachable_call_analysis.release_context(current); this.index_position++; this.completed_context_count++;
			}
			this.unknown_sites.Clear(); this.indirect_sites.Clear(); this.resolved_sites.Clear();
			foreach (var current in this.index_work.Where(work => work.trace is not null)) {
				this.record_indirect_sites(current.method, current.trace!);
				foreach (var call in current.trace!.calls) call_graph_index.add_call_site(result, current.method, call.target, call.instruction.Offset, call.operation, call.potential || current.contextual,
					current.contextual ? "Target resolved in an analyzed caller context; " + (call.reason ?? "argument-dependent value flow") : call.reason ?? (call.potential ? "possible target from value flow or conservative fallback" : "resolved operand or receiver in analyzed context"), current.contextual && call.kind != call_evidence_kind.il_operand ? call_evidence_kind.possible_target : call.kind, current.contextual);
			}
			foreach (var key in this.diagnostics.Keys.Where(key => key.Item3 == graph_diagnostic_kind.analysis_budget && this.diagnostics[key].detail.Contains("saved index frontier", StringComparison.Ordinal)).ToArray()) this.diagnostics.Remove(key);
			if (this.can_resume_index) {
				this.incomplete = true;
				foreach (var current in this.index_work.Skip(this.index_position)) this.report(current.method, 0, graph_diagnostic_kind.analysis_budget, (this.budget.exhaustion ?? "target refinement work slice exhausted") + "; saved index frontier — Continue refinement");
			}
			virtual_calls.AddRange(this.virtual_sites); reflection_calls.AddRange(this.reflection_sites);
			return skipped;
		}

		IEnumerable<MethodDef> order_for_scan(IReadOnlyList<MethodDef> methods) {
			var available = new HashSet<MethodDef>(methods);
			var visited = new HashSet<MethodDef>();
			var pending = new Stack<(MethodDef method, bool exit)>();
			foreach (var seed in methods) {
				pending.Push((method: seed, false));
				while (pending.Count != 0) {
					this.cancellation_token.ThrowIfCancellationRequested();
					var current = pending.Pop();
					if (current.exit) { yield return current.method; continue; }
					if (!visited.Add(current.method)) continue;
					pending.Push((method: current.method, true));
					if (!current.method.HasBody || current.method.Body.Instructions.Count > 200000) continue;
					int position = 0;
					foreach (var instruction in current.method.Body.Instructions) {
						if ((position++ & 255) == 0) this.cancellation_token.ThrowIfCancellationRequested();
						if (instruction.OpCode.Code is not Code.Call and not Code.Newobj || instruction.Operand is not IMethod reference) continue;
						if (this.resolve(reference) is MethodDef target && available.Contains(target) && !visited.Contains(target)) pending.Push((method: target, false));
					}
				}
			}
		}
		internal static bool needs_flow(MethodDef method) => method.Body.ExceptionHandlers.Count != 0 || method.Body.Instructions.Any(instruction => method_flow_runner.may_throw(instruction) || instruction.OpCode.FlowControl == FlowControl.Cond_Branch ||
			instruction.OpCode.Code is Code.Callvirt or Code.Calli or Code.Ldftn or Code.Ldvirtftn or Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un ||
			instruction.Operand is IMethod reference && (reference.Name == "Invoke" || reference.Name == "DynamicInvoke" ||
				call_graph_index.safe_type_name(reference.DeclaringType).StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal) ||
				call_graph_index.safe_type_name(reference.DeclaringType).StartsWith("System.Threading", StringComparison.Ordinal)));

		MethodDef? resolve(IMethod reference) {
			var result = this.workspace.resolve(reference);
			if (result is not null || !this.fallback_attempted.Add(reference)) return result;
			this.loaded_types ??= this.modules.SelectMany(module => module.GetTypes()).GroupBy(type => type.FullName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var declaration = reference.DeclaringType;
			if (declaration is TypeSpec specification && specification.TypeSig is GenericInstSig instance) declaration = instance.GenericType.TypeDefOrRef;
			string name = call_graph_index.safe_type_name(declaration);
			if (!this.loaded_types.TryGetValue(name, out var types)) return null;
			string? assembly = declaration?.DefinitionAssembly?.FullName;
			var candidates = types.Where(type => assembly is null || StringComparer.OrdinalIgnoreCase.Equals(type.Module?.Assembly?.FullName, assembly))
				.SelectMany(type => type.Methods).Where(method => method.Name == reference.Name && new SigComparer().Equals(method.MethodSig, reference.MethodSig)).Take(2).ToArray();
			if (candidates.Length == 1) this.resolved[reference] = result = candidates[0];
			return result;
		}

		symbolic_value field_value(FieldDef? field) {
			if (field?.Constant?.Value is int literal) return symbolic_value.from_integer(this.evaluator.integers.constant(literal));
			if (field?.DeclaringType is TypeDef type && this.modules.Contains(type.Module) && this.initialized.Add(type)) {
				var initializer = this.type_initializer(type);
				if (this.initial_state_known || initializer?.HasBody == true) foreach (var item in type.Fields.Where(item => item.IsStatic && !item.IsLiteral)) this.storage.merge_field(item, symbolic_value.from_integer(this.evaluator.integers.constant(0, item.FieldSig?.Type.ElementType is ElementType.I8 or ElementType.U8)));
				if (initializer?.HasBody == true) {
					var state = new symbolic_state { frame = new flow_address("initializer catalog", type, initializer) };
					try { this.evaluator.evaluate(initializer, Array.Empty<symbolic_value>(), state); }
					catch (flow_suspended_exception) { this.initialized.Remove(type); throw; }
					if (state.conservative_context || this.evaluator.last_failure is not null) foreach (var item in type.Fields.Where(item => item.IsStatic && !item.IsLiteral && !state.memory.ContainsKey(item))) this.storage.set_field(item, symbolic_value.unknown_because("type initializer effects are incomplete"));
					foreach (var pair in state.memory) { if (pair.Key is FieldDef stored) this.storage.set_field(stored, pair.Value); else this.storage.set_slot(pair.Key, pair.Value); }
				}
			}
			var value = this.storage.field_value(field);
			if (value.is_unknown && field is not null && this.inferred_fields.Add(field)) {
				value = this.infer_field(field); if (!value.is_unknown) this.storage.set_field(field, value);
			}
			return value;
		}

		void initialize_for_method(MethodDef method, symbolic_state state, trace? trace, Instruction? site) {
			if (method.IsStaticConstructor) return;
			var type = method.DeclaringType;
			if (!type.IsBeforeFieldInit && (method.IsStatic || method.IsInstanceConstructor || type.IsValueType))
				this.ensure_initialized(method, type, site, state, trace);
		}

		void initialize_for_instruction(MethodDef method, Instruction instruction, symbolic_state state, trace? trace) {
			var code = instruction.OpCode.Code;
			if (code is Code.Ldsfld or Code.Ldsflda or Code.Stsfld && instruction.Operand is IField field_reference) {
				var field = this.storage.resolve_field(field_reference);
				if (field?.DeclaringType is TypeDef type && !field.IsLiteral) this.ensure_initialized(method, type, instruction, state, trace);
			}
			else if (code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp && instruction.Operand is IMethod reference && this.resolve(reference) is MethodDef target) {
				if (target.IsStaticConstructor || target.DeclaringType.IsBeforeFieldInit) return;
				if (target.IsStatic || code == Code.Newobj || target.DeclaringType.IsValueType)
					this.ensure_initialized(method, target.DeclaringType, instruction, state, trace);
			}
		}

		void ensure_initialized(MethodDef source, TypeDef type, Instruction? site, symbolic_state state, trace? trace) {
			if (!this.modules.Contains(type.Module) || state.initialized_types.Contains(type) || state.initializing_types.Contains(type)) return;
			if (state.failed_initializers.Contains(type)) { state.path_terminated = true; return; }
			var initializer = this.type_initializer(type);
			if (initializer is null) { state.initialized_types = state.initialized_types.Add(type); return; }
			if (state.initializing_types.Count >= 32) {
				this.incomplete = true; state.conservative_context = true;
				this.report(source, site?.Offset ?? 0, graph_diagnostic_kind.analysis_budget, "type initialization nesting limit reached"); return;
			}
			this.initialized.Add(type);
			foreach (var field in type.Fields.Where(field => field.IsStatic && !field.IsLiteral))
				if (!state.memory.ContainsKey(field)) state.store(field, symbolic_value.from_integer(this.evaluator.integers.constant(0, field.FieldSig?.Type.ElementType is ElementType.I8 or ElementType.U8)));
			state.initializing_types = state.initializing_types.Add(type);
			var instruction = site ?? source.Body?.Instructions.FirstOrDefault() ?? Instruction.Create(OpCodes.Nop);
			trace?.calls.Add(new call(instruction, initializer, Array.Empty<symbolic_value>(), state,
				!this.initial_state_known || type.IsBeforeFieldInit, "type initializer"));
			bool conservative = state.conservative_context;
			var initialization = new symbolic_state { frame = state.frame }; initialization.import_memory(state); initialization.conservative_context = false;
			this.evaluator.evaluate(initializer, Array.Empty<symbolic_value>(), initialization, instruction);
			state.import_memory(initialization); state.path_terminated = initialization.path_terminated;
			if (conservative) context_call_evaluator.invalidate_unknown_call(state, Array.Empty<symbolic_value>());
			state.initializing_types = state.initializing_types.Remove(type);
			if (state.path_terminated) state.failed_initializers = state.failed_initializers.Add(type);
			else state.initialized_types = state.initialized_types.Add(type);
		}
		symbolic_value infer_field(FieldDef field) {
			var element = field.FieldSig?.Type.ElementType;
			bool is_delegate = this.invocations.is_delegate_type(field.FieldSig?.Type.ToTypeDefOrRef());
			if (!is_delegate && element is not ElementType.I and not ElementType.U and not ElementType.FnPtr && field.FieldSig?.Type.FullName is not "System.IntPtr" and not "System.UIntPtr") return symbolic_value.unknown;
			if (this.field_writers is null) {
				this.field_writers = new Dictionary<FieldDef, List<(MethodDef, int)>>();
				foreach (var method in this.modules.SelectMany(module => module.GetTypes()).SelectMany(type => type.Methods).Where(method => method.HasBody)) {
					this.cancellation_token.ThrowIfCancellationRequested();
					for (int position = 0; position < method.Body.Instructions.Count; position++) {
						var instruction = method.Body.Instructions[position];
						if (instruction.OpCode.Code != Code.Stsfld || this.storage.resolve_field(instruction.Operand as IField) is not FieldDef destination) continue;
						if (!this.field_writers.TryGetValue(destination, out var writers)) this.field_writers[destination] = writers = new List<(MethodDef, int)>();
						writers.Add((method: method, position: position));
					}
				}
			}
			if (!this.field_writers.TryGetValue(field, out var assignments) || assignments.Count == 0) return symbolic_value.unknown;
			var values = new List<symbolic_value> { symbolic_value.from_integer(this.evaluator.integers.constant(0)) };
			foreach (var assignment in assignments) {
				var instructions = assignment.method.Body.Instructions; int position = assignment.position - 1;
				while (position >= 0 && instructions[position].OpCode.Code is Code.Nop or Code.Dup or Code.Conv_I or Code.Conv_U) position--;
				if (position >= 0 && instructions[position].OpCode.Code == Code.Newobj && is_delegate) position--;
				if (position < 0 || instructions[position].OpCode.Code != Code.Ldftn || instructions[position].Operand is not IMethod reference || this.resolve(reference) is not MethodDef target) return symbolic_value.unknown;
				values.Add(is_delegate ? symbolic_value.from_delegate(field.FieldSig!.Type.FullName, field, symbolic_value.from_method(target)).with_receiver(target.IsStatic ? null : symbolic_value.unknown) : symbolic_value.from_method(target));
			}
			return symbolic_value.merge(values);
		}

		public call_graph_slice select(MethodDef root, int depth, int limit, call_graph_filter_options filter, IProgress<call_graph_slice>? progress = null) {
			this.transient_notes.Clear();
			foreach (var key in this.diagnostics.Keys.Where(key => key.Item3 == graph_diagnostic_kind.refinement_progress || key.Item3 == graph_diagnostic_kind.analysis_budget && this.diagnostics[key].detail.Contains("saved frontier", StringComparison.Ordinal)).ToArray()) this.diagnostics.Remove(key);
			this.preserve_structural_calls = filter.preserve_structural_calls;
			if (this.selected_root is null && filter.thorough_refinement) this.budget = new refinement_budget(TimeSpan.FromSeconds(30), long.MaxValue);
			this.initial_state_known = ReferenceEquals(root.Module.EntryPoint, root);
			depth = Math.Max(1, Math.Min(depth, Math.Max(1, limit)));
			limit = Math.Max(2, limit);
			if (this.selected_root is not null && this.selected_root != root) throw new InvalidOperationException("A refinement session belongs to one root");
			if (this.selected_root is null) {
				this.selected_root = root; this.selected_layers[root] = 0;
				enqueue(root, this.unknown_arguments_value(root), 0, new symbolic_state());
			}
			var layers = this.selected_layers;
			int pass_contexts = 0;
			global::dnSpy.CallGraph.Core.reachable_call_analysis.work? stopped = null;
			bool preview_sent = false;
			while (this.frontier.Count != 0) {
				this.cancellation_token.ThrowIfCancellationRequested();
				var current = this.frontier.Peek();
				if (!this.modules.Contains(current.method.Module)) { this.frontier.Dequeue(); continue; }
				if (current.depth >= depth) {
					if (this.workspace.call_structure(current.method, this.cancellation_token).calls.Length != 0) {
						this.view_truncated = true; this.report(current.method, 0, graph_diagnostic_kind.view_depth, "graph depth limit reached; outgoing calls are not displayed");
					}
					this.frontier.Dequeue(); continue;
				}
				if (pass_contexts >= 8000 || this.remaining_steps <= 0 || !this.budget.can_continue()) break;
				global::dnSpy.CallGraph.Core.reachable_call_analysis.trace trace;
				this.walking = current;
				foreach (var key in current.notes.Keys.Where(key => key.Item3 == graph_diagnostic_kind.analysis_budget).ToArray()) current.notes.Remove(key);
				try {
					trace = current.depth + 1 >= depth && this.can_read_structurally(current.method) ? this.read_structural_trace(current.method) : this.walk(current.method, current.arguments, current.memory);
				}
				catch (flow_suspended_exception) { this.helper_interrupted = true; trace = current.trace ?? new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace(); }
				catch (OperationCanceledException) { current.flow = null; current.trace = null; current.notes.Clear(); this.evaluator.clear_cache(true); throw; }
				catch (Exception error) {
					this.incomplete = true; this.report(current.method, 0, graph_diagnostic_kind.unresolved_reference, error.GetType().Name + ": " + error.Message);
					trace = new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace { incomplete = true }; this.retain_structural_calls(current.method, trace);
				}
				finally { this.walking = null; }
				current.trace = trace;
				if (current.flow?.is_paused == true || this.helper_interrupted) {
					if (this.helper_interrupted && current.flow?.is_paused != true) { current.flow = null; current.trace = null; current.notes.Clear(); }
					stopped = current;
					if (this.frontier.Count > 1) this.frontier.Enqueue(this.frontier.Dequeue());
					break;
				}
				foreach (var call in trace.calls)
					call_graph_index.add_call_site(this.call_sites, current.method, call.target, call.instruction.Offset,
						call.operation, call.potential, call.reason, call.kind);
				foreach (var call in trace.calls) {
					var accumulator = this.call_sites[(source: current.method, target: call.target)];
					if (!filter.includes(root, call.target)) continue;
					if (accumulator.count < filter.minimum_call_site_count) continue;
					if (!layers.ContainsKey(call.target)) {
						if (layers.Count >= limit) { this.view_truncated = true; continue; }
						layers.Add(call.target, current.depth + 1);
					}
					enqueue(call.target, call.arguments, current.depth + 1, call.memory);
				}
				reachable_call_analysis.release_context(current); this.frontier.Dequeue(); this.completed_context_count++; pass_contexts++;
				if (!preview_sent && progress is not null) { preview_sent = true; progress.Report(this.snapshot(root, layers, filter, true)); }
			}
			this.call_sites.Clear(); this.unknown_sites.Clear(); this.indirect_sites.Clear(); this.resolved_sites.Clear();
			foreach (var work in this.selected_contexts.Values.Where(work => work.trace is not null)) {
				this.record_indirect_sites(work.method, work.trace!);
				foreach (var call in work.trace!.calls) call_graph_index.add_call_site(this.call_sites, work.method, call.target, call.instruction.Offset, call.operation, call.potential, call.reason, call.kind);
			}
			if (this.frontier.Count != 0) {
				var pending = this.frontier.Peek();
				var fallback = new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace();
				this.transient_report = true; try { this.retain_structural_calls(pending.method, fallback); } finally { this.transient_report = false; }
				this.record_indirect_sites(pending.method, fallback);
				foreach (var call in fallback.calls) {
					call_graph_index.add_call_site(this.call_sites, pending.method, call.target, call.instruction.Offset, call.operation, true, call.reason, call.kind);
					if (filter.includes(root, call.target) && !layers.ContainsKey(call.target) && layers.Count < limit) layers[call.target] = pending.depth + 1;
				}
				var paused = stopped ?? pending;
				this.report(paused.method, 0, graph_diagnostic_kind.analysis_budget, (this.budget.exhaustion ?? this.evaluator.last_failure ?? "target refinement work slice exhausted") + "; saved frontier — Continue refinement");
				this.report(root, 0, graph_diagnostic_kind.refinement_progress, ui_text.pick($"Completed contexts: {this.completed_context_count}; pending: {this.frontier.Count}; cursor: {paused.flow?.position.ToString() ?? "helper"}; {this.evaluator.progress}", $"Обработано контекстов: {this.completed_context_count}; в очереди: {this.frontier.Count}; позиция: {paused.flow?.position.ToString() ?? "помощник"}; {this.evaluator.progress}"));
			}
			else foreach (var key in this.diagnostics.Keys.Where(key => key.Item3 == graph_diagnostic_kind.refinement_progress || key.Item3 == graph_diagnostic_kind.analysis_budget && this.diagnostics[key].detail.Contains("saved frontier", StringComparison.Ordinal)).ToArray()) this.diagnostics.Remove(key);
			return this.snapshot(root, layers, filter, this.view_truncated || this.incomplete || this.frontier.Count != 0);

			void enqueue(MethodDef method, symbolic_value[] arguments, int layer, symbolic_state memory) {
				if (this.selected_contexts.Count >= 8192) {
					arguments = arguments.Select(value => value.integer is not null && !value.integer.is_constant ? symbolic_value.unknown : value).ToArray();
					memory = memory.clone(); memory.conservative_context = true;
					this.report(method, 0, graph_diagnostic_kind.runtime_selection, "Context widening applied; call targets remain conservative candidates");
				}
				string context = this.evaluator.context_key(method, arguments, memory);
				if (this.selected_contexts.ContainsKey(context)) return;
				if (this.selected_contexts.Count >= 32768) { this.incomplete = true; this.report(method, 0, graph_diagnostic_kind.analysis_budget, "context storage limit reached; additional runtime targets remain unknown"); return; }
				var work = new global::dnSpy.CallGraph.Core.reachable_call_analysis.work { method = method, arguments = arguments, depth = layer, memory = memory };
				this.selected_contexts.Add(context, work); this.frontier.Enqueue(work);
			}
		}
		call_graph_slice snapshot(MethodDef root, Dictionary<MethodDef, int> layers, call_graph_filter_options filter, bool truncated) {
			var known_links = this.call_sites.Where(pair => layers.ContainsKey(pair.Key.source))
				.Select(pair => workspace_analysis.link(pair.Key.source, pair.Key.target, pair.Value)).ToArray();
			var links = known_links.Where(link => layers.ContainsKey(link.target) && link.call_site_count >= filter.minimum_call_site_count).ToArray();
			var nodes = layers.Select(pair => new call_graph_node(pair.Key, pair.Value, ReferenceEquals(root, pair.Key), !this.modules.Contains(pair.Key.Module)))
				.OrderBy(node => node.layer).ThenBy(node => call_graph_index.display_name(node.method), StringComparer.Ordinal).ToArray();
			var notes = this.diagnostics.Values.Concat(this.transient_notes.Values).Concat(this.selected_contexts.Values.SelectMany(work => work.notes.Values));
			truncated |= notes.Any(note => note.kind is graph_diagnostic_kind.analysis_budget or graph_diagnostic_kind.unsupported_instruction or graph_diagnostic_kind.invalid_il);
			var slice = new call_graph_slice(nodes, links, truncated, this.unknown_sites.Count,
				notes.Where(item => layers.ContainsKey(item.method)).Distinct().OrderBy(item => item.method.MDToken.Raw).ThenBy(item => item.offset).ToArray(), call_site_info.from_links(known_links));
			return new call_graph_slice(nodes, links, truncated, this.unknown_sites.Count, slice.diagnostics, this.workspace.describe_sites(slice, this.cancellation_token), can_continue_refinement: this.can_resume);
		}

		symbolic_value[] unknown_arguments_value(MethodDef method) {
			if (this.unknown_arguments.TryGetValue(method, out var cached)) return cached;
			return this.unknown_arguments[method] = (method.IsStatic ? Enumerable.Empty<symbolic_value>() : new[] { symbolic_value.from_reference(method.DeclaringType.FullName, new flow_address("parameter", method, -1), false) })
			.Concat(method.MethodSig.Params.Select((type, index) => type.ElementType is ElementType.I4 or ElementType.U4 ? this.evaluator.opaque_integer() :
				type.ElementType == ElementType.ByRef ? symbolic_value.from_storage(new flow_address("parameter address", method, index)) :
				type.ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SZArray or ElementType.Array ? symbolic_value.from_reference(type.FullName, new flow_address("parameter", method, index), true) : symbolic_value.unknown)).ToArray();
		}

		trace walk(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state memory) {
			this.helper_interrupted = false;
			this.evaluator.reset_failure();
			var trace = this.walking?.trace ?? new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace();
			if (!method.HasBody || method.Body.Instructions.Count == 0) {
				this.incomplete = true;
				this.report(method, 0, graph_diagnostic_kind.unsupported_instruction, "method has no inspectable IL body; native, runtime-generated or rewritten implementation may be required");
				return trace;
			}
			this.method_steps = 0; this.method_budget = Math.Min(200000, Math.Min(32768, Math.Max(8192, method.Body.Instructions.Count * 8)) * (1 << this.refinement_pass));
			this.helper_steps = 0; this.helper_budget = this.method_budget;
			this.evaluator.work_scale = 1 << this.refinement_pass;
			string scope = this.evaluator.context_key(method, arguments);
			var initial = new symbolic_state { frame = new flow_address("frame", method, scope) }; initial.import_memory(memory);
			if (method.IsStaticConstructor) initial.initializing_types = initial.initializing_types.Add(method.DeclaringType);
			for (int index = 0; index < arguments.Count; index++) initial.arguments[index] = arguments[index];
			if (this.walking?.flow?.started != true) this.initialize_for_method(method, initial, trace, null);
			if (initial.path_terminated) { if (this.preserve_structural_calls) this.retain_structural_calls(method, trace); return trace; }
			var result = this.runner.run(method, initial, (instruction, state) => {
				this.initialize_for_instruction(method, instruction, state, trace);
				if (state.path_terminated) return;
				trace.reached.Add(instruction.Offset);
				var code = instruction.OpCode.Code;
				if (code is Code.Ldftn or Code.Ldvirtftn && instruction.Operand is IMethod pointer && this.resolve(pointer) is MethodDef referenced) this.report(method, instruction.Offset, graph_diagnostic_kind.address_reference, code == Code.Ldftn ? "ldftn" : "ldvirtftn", referenced);
				if (code == Code.Jmp && instruction.Operand is IMethod reference && this.resolve(reference) is MethodDef target) trace.calls.Add(new call(instruction, target, arguments.ToArray(), state));
				else if (code is Code.Call or Code.Callvirt or Code.Newobj or Code.Calli) this.call_instruction(method, instruction, state, trace, scope);
				else if (!this.evaluator.execute(method, instruction, state, arguments)) {
					this.incomplete = true; trace.incomplete = true;
					this.report(method, instruction.Offset, graph_diagnostic_kind.unsupported_instruction, "unsupported IL operation: " + instruction.OpCode.Name);
					context_call_evaluator.unsupported(instruction, state);
				}
			}, this.consume_step, continuation: this.walking is null ? null : this.walking.flow ??= new method_flow_continuation());
			this.helper_interrupted |= this.evaluator.last_failure?.Contains("budget", StringComparison.Ordinal) == true;
			this.incomplete |= result.incomplete;
			if (result.incomplete) {
				bool invalid = result.failure?.StartsWith("invalid IL", StringComparison.Ordinal) == true;
				if (invalid) trace.calls.Clear();
				this.report(method, 0, invalid ? graph_diagnostic_kind.invalid_il : graph_diagnostic_kind.analysis_budget, result.failure ?? "method analysis budget exhausted");
				if (!invalid && this.walking?.flow?.is_paused != true) this.retain_structural_calls(method, trace);
			}
			if (this.walking?.flow?.is_paused != true && (this.preserve_structural_calls || trace.incomplete && !result.incomplete)) this.retain_structural_calls(method, trace);
			return trace;
		}
		bool can_read_structurally(MethodDef method) => this.preserve_structural_calls && method.HasBody && this.workspace.call_structure(method, this.cancellation_token).address_references.Length == 0 && this.workspace.call_structure(method, this.cancellation_token).calls.All(instruction => {
			if (instruction.OpCode.Code is Code.Calli or Code.Callvirt || instruction.Operand is not IMethod reference) return false;
			if (reference.MethodSig is not MethodSig signature || signature.HasThis || signature.Params.Count != 0 || signature.ParamsAfterSentinel?.Count > 0) return false;
			string type = call_graph_index.safe_type_name(reference.DeclaringType);
			return reference.Name.ToString() is not "Invoke" and not "DynamicInvoke" && !type.StartsWith("System.Threading", StringComparison.Ordinal) && !type.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal);
		});
		trace read_structural_trace(MethodDef method) {
			this.helper_interrupted = false; this.evaluator.reset_failure();
			var trace = new global::dnSpy.CallGraph.Core.reachable_call_analysis.trace(); this.retain_structural_calls(method, trace);
			var memory = new symbolic_state { conservative_context = true };
			void add_initializer(TypeDef? type, Instruction instruction) {
				if (type is null || !this.modules.Contains(type.Module)) return;
				var initializer = this.type_initializer(type);
				if (initializer is not null && initializer != method) trace.calls.Add(new call(instruction, initializer, Array.Empty<symbolic_value>(), memory, true, "type initializer", "structural type initializer dependency; execution is not proven", call_evidence_kind.il_operand));
			}
			if (!method.IsStaticConstructor && !method.DeclaringType.IsBeforeFieldInit && (method.IsStatic || method.IsInstanceConstructor || method.DeclaringType.IsValueType)) add_initializer(method.DeclaringType, method.Body.Instructions.FirstOrDefault() ?? Instruction.Create(OpCodes.Nop));
			int position = 0;
			foreach (var instruction in method.Body.Instructions.Take(200000)) {
				if ((position++ & 255) == 0) this.cancellation_token.ThrowIfCancellationRequested();
				if (instruction.OpCode.Code is Code.Ldsfld or Code.Ldsflda or Code.Stsfld && instruction.Operand is IField reference && this.storage.resolve_field(reference) is FieldDef field && !field.IsLiteral) add_initializer(field.DeclaringType, instruction);
				else if (instruction.OpCode.Code is Code.Call or Code.Newobj or Code.Jmp && instruction.Operand is IMethod called && this.resolve(called) is MethodDef target && !target.IsStaticConstructor && !target.DeclaringType.IsBeforeFieldInit && (target.IsStatic || instruction.OpCode.Code == Code.Newobj || target.DeclaringType.IsValueType)) add_initializer(target.DeclaringType, instruction);
			}
			return trace;
		}
		void retain_structural_calls(MethodDef method, trace trace) {
			if (!this.structural_calls.TryGetValue(method, out var instructions)) {
				if (!method.HasBody) return;
				if (Math.Min(method.Body.Instructions.Count, 200000) > this.remaining_structural_instructions) { this.incomplete = true; this.report(method, 0, graph_diagnostic_kind.analysis_budget, "structural call scan limit reached"); return; }
				this.remaining_structural_instructions -= Math.Min(method.Body.Instructions.Count, 200000);
				try {
					if (this.preserve_structural_calls) {
						var structure = this.workspace.call_structure(method, this.cancellation_token); instructions = structure.calls;
						if (structure.is_incomplete) { this.incomplete = true; this.report(method, 0, graph_diagnostic_kind.analysis_budget, "structural method instruction limit reached"); }
					}
					else {
						var reached = il_reachability.find(method, this.cancellation_token);
						instructions = method.Body.Instructions.Take(200000).Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp && instruction.Operand is IMethod && (reached is null || reached.Contains(instruction.Offset))).ToArray();
					}
					this.structural_calls[method] = instructions;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception error) { this.report(method, 0, graph_diagnostic_kind.invalid_il, "structural call scan failed: " + error.GetType().Name); return; }
			}
			var recorded = trace.calls.Where(call => call.operation != "type initializer").Select(call => call.instruction.Offset).ToHashSet();
			var unknown_memory = new symbolic_state { conservative_context = true };
			bool retained = false;
			foreach (var instruction in instructions) {
				this.cancellation_token.ThrowIfCancellationRequested();
				if (instruction.OpCode.Code == Code.Calli && !trace.indirect_sites.Contains(instruction.Offset)) {
					trace.indirect_sites.Add(instruction.Offset); trace.unresolved_sites.Add(instruction.Offset);
					this.report(method, instruction.Offset, graph_diagnostic_kind.unresolved_pointer, "IL calli target was not resolved in the analyzed context"); continue;
				}
				if (recorded.Contains(instruction.Offset) || instruction.Operand is not IMethod reference) continue;
				if (this.resolve(reference) is not MethodDef target) { this.report(method, instruction.Offset, graph_diagnostic_kind.unresolved_reference, reference.FullName); continue; }
				if (instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) {
					this.virtual_sites.Add(new virtual_call_site(method, target, instruction.Offset, "possible callvirt target"));
					foreach (var implementation in this.virtual_targets_value(method, target, instruction.Offset)) trace.calls.Add(new call(instruction, implementation, this.unknown_arguments_value(implementation), unknown_memory, true));
				}
				if (this.preserve_structural_calls || !target.IsAbstract) trace.calls.Add(new call(instruction, target, this.unknown_arguments_value(target), unknown_memory, true,
					reason: this.preserve_structural_calls ? "IL call operand; retained independently of inferred execution paths" : "structural fallback after incomplete value flow", kind: call_evidence_kind.il_operand));
				retained = true;
			}
			if (retained) this.report(method, 0, this.preserve_structural_calls ? graph_diagnostic_kind.structural_call : graph_diagnostic_kind.analysis_budget,
				this.preserve_structural_calls ? "IL operands retained independently of inferred execution paths" : "value-flow incomplete; structurally reachable calls retained as possible targets");
		}

		void call_instruction(MethodDef source, Instruction instruction, symbolic_state state, trace trace, string scope) {
			bool indirect = instruction.OpCode.Code == Code.Calli;
			var reference = instruction.Operand as IMethod;
			var signature = indirect ? instruction.Operand as MethodSig : reference is null ? null : call_signature.@for(reference);
			if (signature is null) { this.incomplete = true; this.report(source, instruction.Offset, graph_diagnostic_kind.invalid_il, "invalid IL call signature"); state.stack.Clear(); return; }
			var pointer = indirect ? this.invocations.resolve_pointer(reachable_call_analysis.pop(state), state) : symbolic_value.unknown;
			bool constructor = instruction.OpCode.Code == Code.Newobj;
			var arguments = new symbolic_value[call_signature.argument_count(signature, constructor)];
			for (int i = arguments.Length - 1; i >= 0; i--) arguments[i] = reachable_call_analysis.pop(state);
			if (instruction.OpCode.Code == Code.Callvirt && signature.HasThis && arguments.Length != 0 && context_call_evaluator.is_definitely_null(arguments[0])) { state.path_terminated = true; return; }
			MethodDef? declaration = reference is null ? null : this.resolve(reference);
			var targets = indirect ? pointer.methods.Where(target => call_signature.compatible(pointer, target, signature)).ToArray() : declaration is null ? Array.Empty<MethodDef>() : new[] { declaration };
			bool virtual_dispatch = !indirect && instruction.OpCode.Code == Code.Callvirt && declaration?.IsVirtual == true;
			bool exact_dispatch = false;
			if (!indirect && instruction.OpCode.Code == Code.Callvirt && arguments.Length != 0)
				arguments[0] = this.invocations.constrained_receiver(source, instruction, arguments[0], state);
			if (!indirect && instruction.OpCode.Code == Code.Callvirt && arguments.Length != 0 && this.invocations.dispatch_reference(reference!, arguments[0], state) is MethodDef implementation) { targets = new[] { implementation }; exact_dispatch = true; }
			if (virtual_dispatch && !exact_dispatch && (declaration!.IsAbstract || declaration.DeclaringType.IsInterface)) targets = Array.Empty<MethodDef>();
			if (indirect) {
				trace.indirect_sites.Add(instruction.Offset);
				if (targets.Length != 0 && !pointer.may_have_unknown_targets) trace.resolved_sites.Add(instruction.Offset);
				else { trace.unresolved_sites.Add(instruction.Offset); this.report(source, instruction.Offset, graph_diagnostic_kind.unresolved_pointer, pointer.unknown_reason ?? (pointer.methods.Count != 0 ? "function pointer signature or remaining targets could not be resolved" : "function pointer origin is not known in this context")); }
			}
			object allocation_identity = new flow_address("allocation", state.frame, instruction);
			var allocation = this.invocations.new_object(reference, allocation_identity);
			foreach (var target in targets) trace.calls.Add(new call(instruction, target,
				constructor ? new[] { allocation }.Concat(arguments).ToArray() : arguments,
				state, virtual_dispatch && !exact_dispatch || indirect && (pointer.is_ambiguous || pointer.methods.Count != 1 || pointer.virtual_methods.Contains(target))));
			if (virtual_dispatch && !exact_dispatch) {
				this.report(source, instruction.Offset, graph_diagnostic_kind.runtime_selection, "Receiver runtime type is unknown; candidates cover loaded workspace only (open world)");
				this.virtual_sites.Add(new virtual_call_site(source, declaration!, instruction.Offset, "possible callvirt target"));
				foreach (var target in this.virtual_targets_value(source, declaration!, instruction.Offset)) trace.calls.Add(new call(instruction, target, arguments, state, true));
			}
			if (indirect) foreach (var virtual_method in pointer.virtual_methods) {
				this.virtual_sites.Add(new virtual_call_site(source, virtual_method, instruction.Offset, "possible virtual calli"));
				foreach (var target in this.virtual_targets_value(source, virtual_method, instruction.Offset)) trace.calls.Add(new call(instruction, target, arguments, state, true, "possible virtual calli"));
			}
			if (!indirect && reference is not null) {
				var actual_targets = this.invocations.targets(reference, arguments, state).ToArray();
				foreach (var target in actual_targets) trace.calls.Add(new call(instruction, target.method, target.arguments, state, target.potential, target.operation));
				if (reference.Name.ToString() is "Invoke" or "DynamicInvoke" && signature.HasThis && arguments.Length != 0) {
					bool reflection = call_graph_index.safe_type_name(reference.DeclaringType).StartsWith("System.Reflection.", StringComparison.Ordinal);
					bool callable = reflection || this.invocations.is_delegate(reference) || arguments[0].dynamic_names.Count != 0;
					if (arguments[0].lookups.Count != 0) this.reflection_sites.Add(new reflection_target(source, arguments[0].lookups.Select(lookup => lookup.type_name), arguments[0].lookups.Select(lookup => lookup.method_name), arguments[0].methods, instruction.Offset, "reflection invoke"));
					if (callable && (actual_targets.Length == 0 || arguments[0].may_have_unknown_targets)) this.report(source, instruction.Offset, reflection ? graph_diagnostic_kind.unresolved_reflection : graph_diagnostic_kind.unresolved_delegate, arguments[0].unknown_reason ?? "invocation target depends on unresolved values");
				}
				if (targets.Length == 0 && declaration is null && actual_targets.Length == 0 && !this.invocations.is_framework_model(reference)) this.report(source, instruction.Offset, graph_diagnostic_kind.unresolved_reference, reference.FullName);
				if (declaration?.IsPinvokeImpl == true) this.report(source, instruction.Offset, graph_diagnostic_kind.native_boundary, declaration.ImplMap?.Module?.Name + "!" + declaration.ImplMap?.Name, declaration);
			}
			var result = symbolic_value.unknown;
			bool needs_result = constructor || call_signature.returns_value(signature);
			bool needs_effects = needs_result || this.has_continuation(source, instruction);
			if (!needs_effects) return;
			if (reference is null || !this.invocations.try_evaluate(reference, arguments, state, allocation_identity, constructor, out result)) {
				if (constructor) {
					result = allocation;
					if (declaration is not null) this.evaluator.evaluate(declaration, new[] { allocation }.Concat(arguments).ToArray(), state, instruction);
					else context_call_evaluator.invalidate_unknown_call(state, arguments);
				}
				else if (indirect) result = this.evaluator.evaluate_indirect(pointer, signature, arguments, state, instruction);
				else if (targets.Length == 1 && (!virtual_dispatch || exact_dispatch)) {
					result = this.evaluator.evaluate(targets[0], arguments, state, instruction);
					if (result.unknown_reason is string reason) this.report(source, instruction.Offset, graph_diagnostic_kind.unsupported_instruction, reason);
				}
				else context_call_evaluator.invalidate_unknown_call(state, arguments);
			}
			if (constructor || call_signature.returns_value(signature)) state.stack.Add(result);
		}
		bool has_continuation(MethodDef source, Instruction instruction) {
			if (!this.continuation_calls.TryGetValue(source, out var calls)) {
				this.continuation_calls[source] = calls = new HashSet<Instruction>();
				bool continuation = false;
				for (int index = source.Body.Instructions.Count - 1; index >= 0; index--) {
					if ((index & 127) == 0) this.cancellation_token.ThrowIfCancellationRequested();
					var current = source.Body.Instructions[index];
					if (continuation && current.OpCode.FlowControl == FlowControl.Call) calls.Add(current);
					if (current.OpCode.Code != Code.Nop) continuation = current.OpCode.Code is not Code.Ret and not Code.Throw and not Code.Rethrow;
				}
			}
			return calls.Contains(instruction);
		}

		MethodDef[] virtual_targets_value(MethodDef source, MethodDef declaration, uint offset) {
			this.cancellation_token.ThrowIfCancellationRequested();
			return this.workspace.resolver_value.virtual_targets_value(declaration);
		}

		static symbolic_value pop(symbolic_state state) {
			if (state.stack.Count == 0) return symbolic_value.unknown;
			int index = state.stack.Count - 1; var value = state.stack[index]; state.stack.RemoveAt(index); return value;
		}
	}
}
