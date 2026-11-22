using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed class context_call_evaluator {
		readonly Dictionary<IMethod, MethodDef?> resolved_methods;
		readonly Dictionary<(MethodDef method, string arguments, object? memory), symbolic_value> cache = new Dictionary<(MethodDef, string, object?), symbolic_value>();
		readonly Dictionary<object, int> identities = new Dictionary<object, int>();
		readonly HashSet<string> active = new HashSet<string>();
		sealed class return_summary {
			public symbolic_value value = symbolic_value.bottom;
			public symbolic_state? exit;
		}
		readonly Dictionary<string, return_summary> return_summaries = new();
		sealed class helper_continuation {
			public readonly method_flow_continuation flow = new();
			public required MethodDef method;
			public object? frame, site;
			public string? unsupported;
			public bool recursive;
			public bool was_paused;
		}
		readonly Dictionary<string, helper_continuation> helper_continuations = new();
		readonly Dictionary<MethodDef, int> active_methods = new();
		bool recursive_dependency, summaries_changed;
		internal int return_fixed_point_passes { get; private set; }
		internal string progress => string.Join(", ", this.helper_continuations.Values.Where(pending => pending.flow.is_paused).Take(3).Select(pending => $"0x{pending.method.MDToken.Raw:X8}: instruction {pending.flow.position}"));
		readonly Dictionary<integer_expression, symbolic_value> pointer_roots = new Dictionary<integer_expression, symbolic_value>();
		readonly HashSet<integer_expression> pointer_expressions = new HashSet<integer_expression>();
		CancellationToken cancellation_token;
		internal void set_cancellation_token(CancellationToken token) { this.cancellation_token = token; this.runner.set_cancellation_token(token); }
		readonly Func<FieldDef?, symbolic_value> field_value;
		readonly Func<object?, integer_expression?, symbolic_value>? element_value;
		readonly Func<IMethod, MethodDef?>? method_resolver;
		readonly method_flow_runner runner;
		internal flow_memory memory { get; }
		internal invocation_model? invocations { get; set; }
		internal method_summary_index? summaries { get; set; }
		public string? last_failure { get; private set; }
		internal void reset_failure() => this.last_failure = null;
		internal Func<bool>? consume_step { get; set; }
		internal Action<int>? request_budget { get; set; }
		internal Func<MethodDef, bool>? can_analyze { get; set; }
		readonly Dictionary<MethodDef, FieldDef[]> written_fields = new Dictionary<MethodDef, FieldDef[]>();
		readonly HashSet<MethodDef> incomplete_effects = new HashSet<MethodDef>();
		readonly Dictionary<(MethodDef, string, object?), object> frames = new Dictionary<(MethodDef, string, object?), object>();
		readonly Dictionary<MethodDef, HashSet<FieldDef>> read_fields = new Dictionary<MethodDef, HashSet<FieldDef>>();
		internal Action<MethodDef, Instruction, symbolic_state>? before_instruction { get; set; }
		internal Action<MethodDef, symbolic_state>? before_method { get; set; }
		int remaining_steps;
		internal int work_scale { get; set; } = 1;
		int next_identity;
		public integer_expression.factory integers { get; } = new integer_expression.factory();

		public context_call_evaluator(Dictionary<IMethod, MethodDef?> resolved_methods, CancellationToken cancellation_token,
			Func<FieldDef?, symbolic_value> field_value, Func<object?, integer_expression?, symbolic_value>? element_value = null,
			Func<IMethod, MethodDef?>? method_resolver = null, pointer_flow_catalog? catalog = null) {
			this.resolved_methods = resolved_methods;
			this.cancellation_token = cancellation_token;
			this.field_value = field_value;
			this.element_value = element_value;
			this.method_resolver = method_resolver;
			this.runner = new method_flow_runner(cancellation_token);
			this.memory = new flow_memory(catalog ?? new pointer_flow_catalog(), field_value, this.integers);
		}
		public void clear_cache(bool discard_continuations = false) { this.cache.Clear(); if (discard_continuations) { this.return_summaries.Clear(); this.helper_continuations.Clear(); } }
		MethodDef? resolve(IMethod method) => this.method_resolver is null ? call_graph_index.resolve(method, this.resolved_methods) : this.method_resolver(method);

		int identity(object value) {
			if (!this.identities.TryGetValue(value, out int identity)) this.identities.Add(value, identity = ++this.next_identity);
			return identity;
		}
		public symbolic_value opaque_integer(string? reason = null) => symbolic_value.from_integer(this.integers.variable(++this.next_identity)).with_unknown_reason(reason);
		string argument_key(symbolic_value value) =>
			"i" + (value.integer is null ? 0 : this.identity(value.integer)) + ":f" + (value.floating.HasValue ? BitConverter.DoubleToInt64Bits(value.floating.Value).ToString("X16") : "?") + ":o" + (value.storage is null ? 0 : this.identity(value.storage)) +
			":type-object:" + value.represents_type + ":objects" + string.Join(",", value.storage_candidates.Select(this.identity).OrderBy(id => id)) + ":m" + string.Join(",", value.methods.Select(this.identity).OrderBy(id => id)) +
			":v" + string.Join(",", value.virtual_methods.Select(this.identity).OrderBy(id => id)) +
			":s" + string.Join(",", value.strings.Select(this.identity).OrderBy(id => id)) +
			":t" + string.Join(",", value.type_names.Select(this.identity).OrderBy(id => id)) + ":l" + string.Join(",", value.lookups.Select(lookup => lookup.type_name + "::" + lookup.method_name)) +
			":r" + (value.receiver is null ? 0 : this.identity(value.receiver)) + ":p" + string.Join(",", value.pointer_signatures.SelectMany(pair => pair.Value).Select(signature => signature.ToString())) + ":a" + (value.opaque_identity is null ? 0 : this.identity(value.opaque_identity)) + ":d" + string.Join(",", value.dynamic_names) + ":" + value.is_unknown + ":" + value.is_ambiguous + ":" + value.is_boxed + ":" + value.is_exact_type + ":" + value.may_have_unknown_targets + ":" + value.may_be_null + ":kind:" + value.method_kind + ":members:" + string.Join(";", value.members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + this.argument_key(pair.Value)));
		internal string context_key(MethodDef method, IReadOnlyList<symbolic_value> arguments) => this.identity(method) + ":" + string.Join(";", arguments.Select(this.argument_key));
		internal string context_key(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state memory) {
			string key = this.context_key(method, arguments) + ":conservative:" + memory.conservative_context + this.initialization_key(memory);
			if (memory.memory.IsEmpty) return key;
			if (!this.read_fields.TryGetValue(method, out var fields)) {
				if (this.summaries is not null) {
					var effects = this.summaries.get(method, this.cancellation_token);
					fields = new HashSet<FieldDef>(effects.reads);
					if (effects.has_unknown_effects) fields.UnionWith(memory.memory.Keys.OfType<FieldDef>());
					this.read_fields[method] = fields;
				}
				else {
				fields = new HashSet<FieldDef>(); var pending = new Queue<MethodDef>(); var visited = new HashSet<MethodDef>(); pending.Enqueue(method);
				while (pending.Count != 0 && visited.Count < 64) {
					var current = pending.Dequeue(); if (!visited.Add(current) || !current.HasBody || this.can_analyze?.Invoke(current) == false) continue;
					foreach (var instruction in current.Body.Instructions) {
						if (instruction.Operand is IField reference) { try { if (reference.ResolveFieldDef() is FieldDef field) fields.Add(field); } catch (Exception) { } }
						if (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is IMethod called && this.resolve(called) is MethodDef target && target.HasBody && this.can_analyze?.Invoke(target) != false) pending.Enqueue(target);
					}
				}
				if (pending.Count != 0) fields.UnionWith(memory.memory.Keys.OfType<FieldDef>());
				this.read_fields[method] = fields;
				}
			}
			if (this.summaries?.get(method, this.cancellation_token).has_unknown_effects == true) fields.UnionWith(memory.memory.Keys.OfType<FieldDef>());
			var relevant = new HashSet<object>(); var pending_storage = new Queue<object>();
			void visit(symbolic_value value) {
				foreach (var storage in value.possible_storage) {
					if (relevant.Add(storage)) pending_storage.Enqueue(storage);
					var owner = storage is flow_address { kind: "aggregate member" } member ? member.owner : flow_memory.address_root(storage);
					if (owner is not null && relevant.Add(owner)) pending_storage.Enqueue(owner);
				}
				foreach (var member in value.members.Values) visit(member);
			}
			var children = memory.memory.Where(pair => pair.Key is flow_address).ToLookup(pair => ((flow_address)pair.Key).owner);
			foreach (var pair in memory.memory.Where(pair => pair.Key is FieldDef field && fields.Contains(field))) { relevant.Add(pair.Key); visit(pair.Value); }
			foreach (var argument in arguments) visit(argument);
			while (pending_storage.Count != 0) {
				var storage = pending_storage.Dequeue(); visit(memory.load(storage));
				foreach (var pair in children[storage]) { relevant.Add(pair.Key); visit(pair.Value); }
			}
			return key + ":escaped:" + string.Join(",", memory.escaped_storage.Where(relevant.Contains).Select(this.identity).OrderBy(id => id)) + ":heap:" + string.Join(";", memory.memory.Where(pair => relevant.Contains(pair.Key)).Select(pair => (key: this.identity(pair.Key), value: this.argument_key(pair.Value))).OrderBy(pair => pair.key).Select(pair => pair.key + "=" + pair.value));
		}

		public symbolic_value evaluate(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state? caller = null, object? call_site = null) {
			if (this.active.Count != 0) return this.evaluate_core(method, arguments, caller, call_site);
			var input = caller?.clone();
			symbolic_value result = symbolic_value.bottom;
			for (int pass = 0; pass < Math.Min(1024, 32 * this.work_scale); pass++) {
				this.cancellation_token.ThrowIfCancellationRequested();
				this.recursive_dependency = false; this.summaries_changed = false;
				var state = input?.clone();
				result = this.evaluate_core(method, arguments, state, call_site);
				if (caller is not null && state is not null) { caller.import_memory(state); caller.path_terminated = state.path_terminated; caller.no_return_path = state.no_return_path; }
				if (!this.recursive_dependency) return result.is_bottom ? symbolic_value.unknown_because("no returning recursive path established") : result;
				this.return_fixed_point_passes++;
				if (!this.summaries_changed) return result.is_bottom ? symbolic_value.unknown_because("recursive component has no established return value") : result;
				if (this.remaining_steps <= 0 || this.last_failure?.Contains("budget", StringComparison.Ordinal) == true) break;
				this.cache.Clear();
				foreach (var key in this.helper_continuations.Where(pair => pair.Value.recursive).Select(pair => pair.Key).ToArray()) this.helper_continuations.Remove(key);
			}
			this.last_failure = "recursive return fixed point budget exhausted; established targets retained";
			return symbolic_value.merge(new[] { result, symbolic_value.unknown_because(this.last_failure) });
		}
		symbolic_value evaluate_core(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state? caller, object? call_site) {
			if (caller is not null) {
				this.before_method?.Invoke(method, caller);
				if (caller.path_terminated) return symbolic_value.unknown_because("type initialization failed");
			}
			if (this.can_analyze?.Invoke(method) == false) { if (caller is not null) this.invalidate_effects(method, arguments, caller); return symbolic_value.unknown_because("method body is outside the loaded workspace"); }
			if (method.HasBody) this.request_budget?.Invoke(Math.Min(200000, method.Body.Instructions.Count * 2));
			if (this.active.Count == 0) { this.remaining_steps = Math.Min(4000000, 120000 * this.work_scale); this.last_failure = null; }
			if (this.active.Count >= 64 || !method.HasBody || this.remaining_steps <= 0) {
				this.last_failure = !method.HasBody ? "method has no IL body" : this.active.Count >= 64 ? "helper context depth limit reached; remaining return targets are unknown" : "helper analysis budget exhausted";
				if (caller is not null) this.invalidate_effects(method, arguments, caller);
				return symbolic_value.unknown_because(this.last_failure);
			}
			var key = (method: method, string.Join(";", arguments.Select(this.argument_key)) + ":conservative:" + (caller?.conservative_context == true) + (caller is null ? string.Empty : this.initialization_key(caller)), caller is null || caller.memory.IsEmpty ? null : caller.memory_stamp);
			if (this.cache.TryGetValue(key, out var cached)) return cached;
			var normalized = arguments.Select(value => value.integer is not null && !value.integer.is_constant ? symbolic_value.unknown : value).ToArray();
			bool recursive_method = this.summaries?.get(method, this.cancellation_token).is_recursive == true || this.active_methods.ContainsKey(method);
			string recursion_key = caller is null || !recursive_method ? this.context_key(method, normalized) : this.context_key(method, normalized, caller);
			if (!this.active.Add(recursion_key)) {
				this.recursive_dependency = true;
				var summary = this.return_summaries.GetValueOrDefault(recursion_key);
				var approximation = summary?.value ?? symbolic_value.bottom;
				if (caller is not null && summary?.exit is not null) caller.import_memory(summary.exit);
				if (caller is not null && approximation.is_bottom) caller.no_return_path = true;
				return approximation;
			}
			this.active_methods[method] = this.active_methods.GetValueOrDefault(method) + 1;
			try {
				var frame_key = (method: method, key.Item2, caller is null ? null : (object)new flow_address("callsite", caller.frame, call_site ?? method));
				if (!this.frames.TryGetValue(frame_key, out var frame)) this.frames[frame_key] = frame = new object();
				var state = new symbolic_state { frame = frame };
				if (caller is not null) state.import_memory(caller);
				for (int index = 0; index < arguments.Count; index++) state.arguments[index] = arguments[index];
				object stamp = state.memory_stamp;
				string resume_key_value() => (caller is null ? this.context_key(method, arguments) : this.context_key(method, arguments, caller)) + ":call-frame:" + (caller is null ? 0 : this.identity(caller.frame)) + ":call-site:" + (call_site is null ? 0 : this.identity(call_site));
				string? resume_key = this.helper_continuations.Values.Any(pending => pending.method == method && Equals(pending.frame, caller?.frame) && Equals(pending.site, call_site)) ? resume_key_value() : null;
				var pending = resume_key is not null && this.helper_continuations.TryGetValue(resume_key, out var existing) ? existing : new helper_continuation { method = method, frame = caller?.frame, site = call_site };
				this.recursive_dependency |= pending.recursive;
				var flow = this.run(method, arguments, state, pending);
				pending.recursive |= this.recursive_dependency;
				pending.was_paused |= pending.flow.is_paused;
				bool nested_budget = this.last_failure?.Contains("budget", StringComparison.Ordinal) == true;
				if (pending.flow.is_paused || pending.was_paused && !flow.incomplete && !nested_budget && pending.flow.is_complete) {
					if (this.helper_continuations.Count >= 256) {
						var expired = this.helper_continuations.FirstOrDefault(pair => pair.Value.flow.is_complete).Key;
						if (expired is not null) this.helper_continuations.Remove(expired);
					}
					resume_key ??= resume_key_value();
					this.helper_continuations[resume_key] = pending;
				}
				else if (resume_key is not null) this.helper_continuations.Remove(resume_key);
				if (pending.flow.is_paused) throw new flow_suspended_exception();
				var result = flow.incomplete ? symbolic_value.merge(new[] { flow.value, symbolic_value.unknown_because(flow.failure ?? "helper analysis budget exhausted") }) : flow.value;
				if (recursive_method && !flow.incomplete && !nested_budget && this.return_summaries.Count < 16000) {
					if (!this.return_summaries.TryGetValue(recursion_key, out var summary)) this.return_summaries[recursion_key] = summary = new return_summary();
					var next = symbolic_value.merge(new[] { summary.value, result });
					if (!next.same_as(summary.value)) { summary.value = next; this.summaries_changed = true; }
					if (flow.exit is not null) {
						if (summary.exit is null) { summary.exit = flow.exit.clone(); this.summaries_changed = true; }
						else this.summaries_changed |= summary.exit.merge_from(flow.exit);
					}
				}
				if (flow.incomplete) this.last_failure = "helper " + method.FullName + ": " + flow.failure;
				if (caller is not null) {
					if (!flow.incomplete && flow.exit is not null) caller.import_memory(flow.exit);
					else if (!flow.incomplete && flow.exception_exit is not null) { caller.import_memory(flow.exception_exit); caller.path_terminated = true; }
					else if (!flow.incomplete && result.is_bottom) caller.no_return_path = true;
					else this.invalidate_effects(method, arguments, caller);
				}
				if (!flow.incomplete && !nested_budget && flow.exit is not null && ReferenceEquals(stamp, flow.exit.memory_stamp) && state.initialized_types.SetEquals(flow.exit.initialized_types) && !result.is_ambiguous && result.storage is null && result.receiver is null &&
					(result.methods.Count == 1 || result.integer?.is_constant == true || !call_signature.returns_value(method.MethodSig) && flow.exception_exit is null) && this.cache.Count < 16000) this.cache[key] = result;
				return result;
			}
			finally { this.active.Remove(recursion_key); if (--this.active_methods[method] == 0) this.active_methods.Remove(method); }
		}
		void invalidate_effects(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state state) {
			if (!method.HasBody || this.can_analyze?.Invoke(method) == false) { context_call_evaluator.invalidate_unknown_call(state, arguments); return; }
			if (this.summaries is not null && !this.written_fields.ContainsKey(method)) {
				var summary = this.summaries.get(method, this.cancellation_token);
				this.written_fields[method] = summary.writes;
				if (summary.has_unknown_effects) this.incomplete_effects.Add(method);
			}
			if (!this.written_fields.TryGetValue(method, out var fields)) {
				var collected = new HashSet<FieldDef>(); var visited = new HashSet<MethodDef>(); var queue = new Queue<MethodDef>(); queue.Enqueue(method);
				int steps = 0;
				while (queue.Count != 0 && visited.Count < 128 && steps < 16000) {
					var current = queue.Dequeue(); if (!visited.Add(current) || !current.HasBody || this.can_analyze?.Invoke(current) == false) continue;
					foreach (var instruction in current.Body.Instructions) {
						if (++steps > 16000) break;
						if (instruction.OpCode.Code is Code.Calli or Code.Cpblk or Code.Initblk or Code.Stobj or Code.Cpobj or Code.Initobj || instruction.OpCode.Name.StartsWith("stind", StringComparison.Ordinal)) this.incomplete_effects.Add(method);
						if (instruction.OpCode.Code is Code.Stsfld or Code.Stfld && instruction.Operand is IField reference) { try { if (reference.ResolveFieldDef() is FieldDef field) collected.Add(field); } catch (Exception) { } }
						if (instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is IMethod called) {
							var target = this.resolve(called);
							if (target is null || !target.HasBody || this.can_analyze?.Invoke(target) == false || instruction.OpCode.Code == Code.Callvirt && target.IsVirtual) this.incomplete_effects.Add(method);
							else queue.Enqueue(target);
						}
					}
				}
				fields = collected.ToArray(); this.written_fields[method] = fields;
				if (queue.Count != 0 || steps >= 16000) this.incomplete_effects.Add(method);
			}
			var affected = new HashSet<FieldDef>(fields); bool all = this.incomplete_effects.Contains(method);
			if (all) { context_call_evaluator.invalidate_unknown_call(state, arguments); return; }
			if (fields.Length == 0) return;
			var unknown = symbolic_value.unknown_because("field may have been changed by an incomplete helper analysis");
			var argument_owners = arguments.Select(value => flow_memory.read_object(state, value).storage).Where(owner => owner is not null).ToHashSet();
			foreach (var key in state.memory.Keys.Where(key => key is flow_address slot && argument_owners.Contains(slot.owner) && (slot.kind.StartsWith("collection ", StringComparison.Ordinal) || slot.kind is "element" or "length")).ToArray()) state.store(key, symbolic_value.merge(new[] { state.load(key), symbolic_value.unknown_because("container may have been changed by an incomplete helper analysis") }));
			foreach (var field in fields) if (field.IsStatic) state.store(field, unknown);
			foreach (var key in state.memory.Keys.Where(key => key is flow_address slot && (slot.kind == "field" && slot.slot is FieldDef field && (all || affected.Contains(field)) || all && slot.kind == "element") || all && key is FieldDef).ToArray()) state.store(key, unknown);
			int offset = method.IsStatic ? 0 : 1;
			for (int index = 0; index < method.MethodSig.Params.Count && index + offset < arguments.Count; index++) if (method.MethodSig.Params[index].ElementType == ElementType.ByRef && arguments[index + offset].storage is object address) state.store(address, symbolic_value.unknown_because("by-reference value may have been changed by an incomplete helper analysis"));
		}

		method_flow_result run(MethodDef method, IReadOnlyList<symbolic_value> arguments, symbolic_state initial, helper_continuation pending) {
			var flow = this.runner.run(method, initial, (instruction, state) => {
				this.before_instruction?.Invoke(method, instruction, state);
				if (state.path_terminated) return;
				if (instruction.OpCode.Code == Code.Jmp && instruction.Operand is IMethod reference && this.resolve(reference) is MethodDef target) { state.stack.Add(this.evaluate(target, arguments, state)); return; }
				if (!this.execute(method, instruction, state, arguments)) {
					this.last_failure = "unsupported IL operation: " + instruction.OpCode.Name;
					pending.unsupported ??= this.last_failure;
					context_call_evaluator.unsupported(instruction, state);
				}
			}, () => --this.remaining_steps >= 0 && (this.consume_step?.Invoke() ?? true), maximum_ambiguous_branches:
				method.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn) ? 32 : !call_signature.returns_value(method.MethodSig) ? 0 : 2, continuation: pending.flow);
			if (pending.unsupported is not null) { flow.incomplete = true; flow.failure ??= pending.unsupported; }
			return flow;
		}

		string initialization_key(symbolic_state state) => ":initialized:" + string.Join(",", state.initialized_types.Select(this.identity).OrderBy(value => value)) +
			":initializing:" + string.Join(",", state.initializing_types.Select(this.identity).OrderBy(value => value)) + ":failed-init:" + string.Join(",", state.failed_initializers.Select(this.identity).OrderBy(value => value));

		internal bool execute(MethodDef method, Instruction instruction, symbolic_state state, IReadOnlyList<symbolic_value> arguments) {
			var code = instruction.OpCode.Code;
			if (this.memory.execute(method, instruction, state, arguments)) return true;
			if (instruction.IsLdcI4()) { state.stack.Add(symbolic_value.from_integer(this.integers.constant(instruction.GetLdcI4Value()))); return true; }
			int local = context_call_evaluator.local_index(instruction);
			if (local >= 0) {
				var address = flow_memory.local(state, local);
				if (code == Code.Stloc || code == Code.Stloc_S || code >= Code.Stloc_0 && code <= Code.Stloc_3) {
					var value = context_call_evaluator.pop(state); state.locals[local] = value; if (state.memory.ContainsKey(address)) state.store(address, value);
				}
				else state.stack.Add(state.memory.TryGetValue(address, out var stored) ? stored : state.locals.TryGetValue(local, out var value) ? value : local < method.Body.Variables.Count ? this.memory.initial_local(method, method.Body.Variables[local], address) : symbolic_value.unknown);
				return true;
			}
			int argument = context_call_evaluator.argument_index(instruction);
			if (argument >= 0) { state.stack.Add(state.memory.TryGetValue(flow_memory.argument(state, argument), out var stored) ? stored : state.arguments.TryGetValue(argument, out var changed) ? changed : argument < arguments.Count ? arguments[argument] : symbolic_value.unknown); return true; }
			switch (code) {
			case Code.Nop: case Code.Volatile: case Code.Unaligned: case Code.Tailcall: case Code.Readonly: case Code.Constrained: return true;
			case Code.Ldc_I8: state.stack.Add(symbolic_value.from_integer(this.integers.constant((long)instruction.Operand, true))); return true;
			case Code.Ldc_R4: state.stack.Add(symbolic_value.from_floating((float)instruction.Operand)); return true;
			case Code.Ldc_R8: state.stack.Add(symbolic_value.from_floating((double)instruction.Operand)); return true;
			case Code.Ldnull: state.stack.Add(symbolic_value.from_integer(this.integers.constant(0))); return true;
			case Code.Ldstr: state.stack.Add(symbolic_value.from_string((string)instruction.Operand)); return true;
			case Code.Dup: state.stack.Add(state.stack.Count != 0 ? state.stack[state.stack.Count - 1] : symbolic_value.unknown); return true;
			case Code.Pop: context_call_evaluator.pop(state); return true;
			case Code.Box: state.stack.Add(symbolic_value.box(context_call_evaluator.pop(state))); return true;
			case Code.Unbox_Any: state.stack.Add(symbolic_value.unbox(context_call_evaluator.pop(state))); return true;
			case Code.Castclass: state.stack.Add(context_call_evaluator.pop(state)); return true;
			case Code.Ldtoken: {
				var value = instruction.Operand is IMethod reference && this.resolve(reference) is MethodDef target ? symbolic_value.from_method(target, signature: call_signature.@for(reference)).as_method_kind(method_value_kind.handle) : instruction.Operand is ITypeDefOrRef type ? symbolic_value.from_type(type.FullName) : symbolic_value.unknown;
				state.stack.Add(value); return true;
			}
			case Code.Ldelem_I: case Code.Ldelem_Ref: {
				var index = context_call_evaluator.pop(state).integer; var array = context_call_evaluator.pop(state).storage;
				state.stack.Add(this.element_value?.Invoke(array, index) ?? symbolic_value.unknown); return true;
			}
			case Code.Ldftn: case Code.Ldvirtftn: {
				var receiver = code == Code.Ldvirtftn ? flow_memory.read_object(state, context_call_evaluator.pop(state)) : null;
				if (receiver is not null && context_call_evaluator.is_definitely_null(receiver)) { state.path_terminated = true; return true; }
				var target = instruction.Operand is IMethod reference ? this.resolve(reference) : null;
				state.stack.Add(target is null ? symbolic_value.unknown : symbolic_value.from_method(target, code == Code.Ldvirtftn, receiver, call_signature.@for((IMethod)instruction.Operand)));
				return true;
			}
			case Code.Ldsfld: case Code.Ldsflda: {
				var field = (instruction.Operand as IField)?.ResolveFieldDef();
				if (field?.Constant?.Value is int constant) state.stack.Add(symbolic_value.from_integer(this.integers.constant(constant)));
				else state.stack.Add(code == Code.Ldsfld ? this.field_value(field) : field is null ? symbolic_value.unknown : symbolic_value.from_storage(field));
				return true;
			}
			case Code.Calli: {
				if (instruction.Operand is not MethodSig signature) return false;
				var pointer = context_call_evaluator.pop(state); if (this.invocations is not null) pointer = this.invocations.resolve_pointer(pointer, state);
				var values = new symbolic_value[call_signature.argument_count(signature)];
				for (int index = values.Length - 1; index >= 0; index--) values[index] = context_call_evaluator.pop(state);
				var result = this.evaluate_indirect(pointer, signature, values, state, instruction);
				if (call_signature.returns_value(signature)) state.stack.Add(result);
				return true;
			}
			case Code.Call: case Code.Callvirt: case Code.Newobj: {
				if (instruction.Operand is not IMethod reference || reference.MethodSig is null) return false;
				var signature = call_signature.@for(reference);
				bool constructor = code == Code.Newobj;
				var values = new symbolic_value[call_signature.argument_count(signature, constructor)];
				for (int index = values.Length - 1; index >= 0; index--) values[index] = context_call_evaluator.pop(state);
				if (code == Code.Callvirt && signature.HasThis && values.Length != 0 && context_call_evaluator.is_definitely_null(values[0])) { state.path_terminated = true; return true; }
				var target = this.resolve(reference);
				if (code == Code.Callvirt && values.Length != 0) {
					if (this.invocations is not null) values[0] = this.invocations.constrained_receiver(method, instruction, values[0], state);
					target = this.invocations?.dispatch_reference(reference, values[0], state);
				}
				object identity = new flow_address("allocation", state.frame, instruction);
				var result = symbolic_value.unknown;
				if (this.invocations?.try_evaluate(reference, values, state, identity, constructor, out result) != true) {
					if (constructor) {
						result = this.invocations?.new_object(reference, identity) ?? symbolic_value.from_object(call_graph_index.safe_type_name(reference.DeclaringType), identity);
						if (target is not null) this.evaluate(target, new[] { result }.Concat(values).ToArray(), state, instruction);
					}
					else if (target is not null) result = this.evaluate(target, values, state, instruction);
					else { context_call_evaluator.invalidate_unknown_call(state, values); result = symbolic_value.unknown_because("method reference or runtime receiver could not be resolved: " + reference.FullName); }
				}
				if (constructor || call_signature.returns_value(signature)) {
					if (result.integer is null && result.methods.Count == 0 && context_call_evaluator.is_integer(signature.RetType)) result = this.opaque_integer(result.unknown_reason);
					state.stack.Add(result);
				}
				return true;
			}
			case Code.Neg: case Code.Not: {
				var operand = context_call_evaluator.pop(state); state.stack.Add(code == Code.Neg && operand.floating.HasValue ? symbolic_value.from_floating(-operand.floating.Value) : symbolic_value.from_integer(this.integers.unary(code, operand.integer))); return true;
			}
			case Code.Conv_I: case Code.Conv_U: case Code.Conv_I1: case Code.Conv_U1: case Code.Conv_I2: case Code.Conv_U2: case Code.Conv_I4: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U8: {
				var value = context_call_evaluator.pop(state);
				if (code is Code.Conv_I or Code.Conv_U && flow_memory.is_address(value)) { state.stack.Add(value); return true; }
				if (value.methods.Count != 0) {
					if (code is Code.Conv_I1 or Code.Conv_U1 or Code.Conv_I2 or Code.Conv_U2 || code is Code.Conv_I4 or Code.Conv_U4 && !method.Module.Is32BitRequired) value = symbolic_value.unknown_because("function pointer was narrowed to an unsafe width");
				}
				else value = symbolic_value.from_integer(this.integers.convert(code, value.integer, method.Module.GetPointerSize(0)));
				state.stack.Add(value); return true;
			}
			case Code.Add: case Code.Sub: case Code.Mul: case Code.Xor: case Code.And: case Code.Or:
			case Code.Shl: case Code.Shr: case Code.Shr_Un: case Code.Div: case Code.Div_Un: case Code.Rem: case Code.Rem_Un:
			case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un: {
				var right = context_call_evaluator.pop(state); var left = context_call_evaluator.pop(state);
				if (code is Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un && !left.floating.HasValue && !right.floating.HasValue) {
					bool zero_divisor = right.integer?.is_zero == true;
					bool signed_overflow = code == Code.Div && left.integer?.is_constant == true && right.integer?.is_constant == true &&
						right.integer.value == -1 && (left.integer.bit_width == 64 && left.integer.value == long.MinValue ||
						left.integer.bit_width == 32 && left.integer.value == int.MinValue);
					if (zero_divisor || signed_overflow) { state.path_terminated = true; return true; }
				}
				state.stack.Add(this.binary(code, left, right)); return true;
			}
			default: return false;
			}
		}
		internal static void invalidate_unknown_call(symbolic_state state, IReadOnlyList<symbolic_value> arguments) {
			state.conservative_context = true;
			var unknown = symbolic_value.unknown_because("value may have been changed by an unresolved call");
			var reachable = new HashSet<object>(state.escaped_storage);
			var pending = new Queue<object>();
			var children = state.memory.Where(pair => pair.Key is flow_address).ToLookup(pair => ((flow_address)pair.Key).owner);
			void visit(symbolic_value value) {
				foreach (var address in value.possible_storage) if (reachable.Add(address)) pending.Enqueue(address);
				foreach (var member in value.members.Values) visit(member);
			}
			foreach (var address in state.escaped_storage) pending.Enqueue(address);
			foreach (var pair in state.memory.Where(pair => pair.Key is FieldDef)) visit(pair.Value);
			foreach (var value in arguments) visit(value);
			while (pending.Count != 0) {
				var owner = pending.Dequeue();
				visit(state.load(owner));
				foreach (var pair in children[owner]) visit(pair.Value);
			}
			state.escaped_storage = state.escaped_storage.Union(reachable);
			foreach (var key in state.memory.Keys.Where(key => key is FieldDef || key is flow_address slot && slot.kind is not "local" and not "argument" and not "length" && reachable.Contains(slot.owner)).ToArray()) state.store(key, symbolic_value.merge(new[] { state.load(key), unknown }));
			foreach (var value in arguments) if (value.storage is flow_address slot && slot.kind is "local" or "argument" or "aggregate member") state.store(slot, symbolic_value.merge(new[] { state.load(slot), unknown }));
			foreach (var value in arguments) if (flow_memory.address_root(value.storage) is FieldDef field) state.store(field, symbolic_value.merge(new[] { state.load(field), unknown }));
			foreach (var value in arguments.Where(value => value.type_names.Count == 0 && !value.is_boxed)) foreach (var address in value.possible_storage) if (address is flow_address or FieldDef) state.store(address, symbolic_value.merge(new[] { state.load(address), unknown }));
		}
		internal static bool is_definitely_null(symbolic_value value) => !value.is_boxed && !value.may_have_unknown_targets && value.integer?.is_zero == true;
		internal static void unsupported(Instruction instruction, symbolic_state state) {
			instruction.CalculateStackUsage(out int pushes, out int pops);
			if (instruction.OpCode.Code is Code.Cpblk or Code.Initblk or Code.Stind_R4 or Code.Stind_R8) context_call_evaluator.invalidate_unknown_call(state, state.stack.ToArray());
			if (pops < 0) state.stack.Clear(); else for (int index = 0; index < pops; index++) context_call_evaluator.pop(state);
			for (int index = 0; index < pushes; index++) state.stack.Add(symbolic_value.unknown_because("unsupported IL operation: " + instruction.OpCode.Name));
		}
		internal symbolic_value evaluate_indirect(symbolic_value pointer, MethodSig signature, IReadOnlyList<symbolic_value> arguments, symbolic_state state, object call_site) {
			var targets = pointer.methods.Where(target => call_signature.compatible(pointer, target, signature)).ToArray();
			var results = new List<symbolic_value>(); symbolic_state? normal = null, exceptional = null;
			foreach (var target in targets) {
				var branch = state.clone(); var result = this.evaluate(target, arguments, branch, call_site);
				if (branch.path_terminated) { if (exceptional is null) exceptional = branch; else exceptional.merge_from(branch); }
				else { results.Add(result); if (normal is null) normal = branch; else normal.merge_from(branch); }
			}
			if (targets.Length == 0 || pointer.may_have_unknown_targets || targets.Length != pointer.methods.Count) {
				this.last_failure = "function pointer origin or signature is unresolved";
				results.Add(symbolic_value.unknown_because(this.last_failure));
				var branch = state.clone(); context_call_evaluator.invalidate_unknown_call(branch, arguments);
				if (normal is null) normal = branch; else normal.merge_from(branch);
			}
			if ((normal ?? exceptional) is symbolic_state exit) state.import_memory(exit);
			state.path_terminated = normal is null && exceptional is not null;
			return symbolic_value.merge(results);
		}

		public symbolic_value binary(Code code, symbolic_value left, symbolic_value right) {
			if (!left.is_boxed && !right.is_boxed && left.floating.HasValue && right.floating.HasValue) {
				if (floating_constant.compare(code, left.floating.Value, right.floating.Value) is bool comparison) return symbolic_value.from_integer(this.integers.constant(comparison ? 1 : 0));
				return floating_constant.binary(code, left.floating.Value, right.floating.Value) is double floating ? symbolic_value.from_floating(floating) : symbolic_value.unknown_because("floating operation is not exactly representable in the supported precision model");
			}
			if (code is Code.Add or Code.Sub && flow_memory.is_address(left) && right.integer?.is_constant == true) return flow_memory.offset(left, right.integer.value, code == Code.Sub);
			if (code == Code.Add && flow_memory.is_address(right) && left.integer?.is_constant == true) return flow_memory.offset(right, left.integer.value, false);
			if (code is Code.Ceq or Code.Cgt_Un) {
				bool left_nonnull = !left.is_unknown && !left.is_ambiguous && !left.may_be_null && (left.storage is not null || left.is_boxed || left.methods.Count != 0 || left.strings.Count != 0);
				bool right_null = right.integer?.is_zero == true && !right.is_boxed;
				if (left_nonnull && right_null) return symbolic_value.from_integer(this.integers.constant(code == Code.Ceq ? 0 : 1));
				if (code == Code.Ceq && (left.storage is not null && Equals(left.storage, right.storage) || left.opaque_identity is not null && Equals(left.opaque_identity, right.opaque_identity))) return symbolic_value.from_integer(this.integers.constant(1));
			}
			var a = this.expression(left); var b = this.expression(right);
			bool pointer = a is not null && this.pointer_expressions.Contains(a) || b is not null && this.pointer_expressions.Contains(b);
			if (pointer && code != Code.Xor && code != Code.Add && code != Code.Sub) return symbolic_value.unknown;
			var expression = this.integers.binary(code, a, b);
			if (pointer && expression is not null) this.pointer_expressions.Add(expression);
			return expression is not null && this.pointer_roots.TryGetValue(expression, out var target) ? target : symbolic_value.from_integer(expression);
		}
		integer_expression? expression(symbolic_value value) {
			if (value.is_boxed) return null;
			if (value.methods.Count != 0 && value.method_kind != method_value_kind.pointer) return null;
			if (value.methods.Count != 1) return value.integer;
			var method = value.methods.Single();
			var expression = this.integers.variable(this.identity((method: method, value.virtual_methods.Contains(method))));
			this.pointer_roots[expression] = value; this.pointer_expressions.Add(expression); return expression;
		}
		static bool is_integer(TypeSig type) => type.ElementType == ElementType.I4 || type.ElementType == ElementType.U4;
		static symbolic_value pop(symbolic_state state) {
			if (state.stack.Count == 0) return symbolic_value.unknown;
			int index = state.stack.Count - 1; var value = state.stack[index]; state.stack.RemoveAt(index); return value;
		}
		internal static bool? branch(Code code, symbolic_state state) {
			var right_value = context_call_evaluator.pop(state);
			var right = right_value.is_boxed ? null : right_value.integer;
			if (code == Code.Brtrue || code == Code.Brtrue_S || code == Code.Brfalse || code == Code.Brfalse_S) {
				bool is_false = code == Code.Brfalse || code == Code.Brfalse_S;
				if (right?.is_constant == true) return right.is_zero == is_false;
				if (!right_value.is_unknown && !right_value.is_ambiguous && !right_value.may_be_null && (right_value.is_boxed || right_value.storage is not null || right_value.strings.Count != 0 || right_value.methods.Count != 0)) return !is_false;
				return null;
			}
			var left_value = context_call_evaluator.pop(state);
			if (!left_value.is_boxed && !right_value.is_boxed && left_value.floating.HasValue && right_value.floating.HasValue) return floating_constant.compare(code, left_value.floating.Value, right_value.floating.Value);
			if (left_value.opaque_identity is not null && Equals(left_value.opaque_identity, right_value.opaque_identity) || left_value.storage is not null && Equals(left_value.storage, right_value.storage)) {
				if (code is Code.Beq or Code.Beq_S) return true;
				if (code is Code.Bne_Un or Code.Bne_Un_S) return false;
			}
			var left = left_value.integer;
			if (left?.is_constant != true || right?.is_constant != true) return null;
			long a = left.value, b = right.value;
			if ((left.bit_width == 0 || right.bit_width == 0) && (a < 0 || b < 0) && code is Code.Bge_Un or Code.Bge_Un_S or Code.Bgt_Un or Code.Bgt_Un_S or Code.Ble_Un or Code.Ble_Un_S or Code.Blt_Un or Code.Blt_Un_S) return null;
			bool wide = left.bit_width == 64 || right.bit_width == 64;
			return code switch {
				Code.Beq or Code.Beq_S => a == b, Code.Bne_Un or Code.Bne_Un_S => a != b,
				Code.Bge or Code.Bge_S => a >= b, Code.Bgt or Code.Bgt_S => a > b,
				Code.Ble or Code.Ble_S => a <= b, Code.Blt or Code.Blt_S => a < b,
				Code.Bge_Un or Code.Bge_Un_S => wide ? (ulong)a >= (ulong)b : (uint)a >= (uint)b,
				Code.Bgt_Un or Code.Bgt_Un_S => wide ? (ulong)a > (ulong)b : (uint)a > (uint)b,
				Code.Ble_Un or Code.Ble_Un_S => wide ? (ulong)a <= (ulong)b : (uint)a <= (uint)b,
				Code.Blt_Un or Code.Blt_Un_S => wide ? (ulong)a < (ulong)b : (uint)a < (uint)b,
				_ => null,
			};
		}
		static int local_index(Instruction instruction) => instruction.OpCode.Code switch {
			Code.Ldloc_0 or Code.Stloc_0 => 0, Code.Ldloc_1 or Code.Stloc_1 => 1,
			Code.Ldloc_2 or Code.Stloc_2 => 2, Code.Ldloc_3 or Code.Stloc_3 => 3,
			Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S => instruction.Operand is Local local ? local.Index : -1, _ => -1,
		};
		static int argument_index(Instruction instruction) => instruction.OpCode.Code switch {
			Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
			Code.Ldarg or Code.Ldarg_S => instruction.Operand is Parameter parameter ? parameter.Index : -1, _ => -1,
		};
	}
}
