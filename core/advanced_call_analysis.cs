using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record delegate_entry(MethodDef method, symbolic_value? receiver);
	sealed record finally_continuation(int position, finally_continuation? next);
	enum method_value_kind { pointer, handle, reflection, @delegate, mixed }
	sealed class reflection_lookup : IEquatable<reflection_lookup> {
		public string type_name { get; }
		public string method_name { get; }

		public reflection_lookup(string type_name, string method_name) {
			this.type_name = type_name;
			this.method_name = method_name;
		}

		public bool Equals(reflection_lookup? other) => other is not null &&
			StringComparer.Ordinal.Equals(this.type_name, other.type_name) &&
			StringComparer.Ordinal.Equals(this.method_name, other.method_name);
		public override bool Equals(object? obj) => Equals(obj as reflection_lookup);
		public override int GetHashCode() => HashCode.Combine(
			StringComparer.Ordinal.GetHashCode(this.type_name), StringComparer.Ordinal.GetHashCode(this.method_name));
	}

	sealed class symbolic_value {
		public static readonly symbolic_value unknown = new symbolic_value(true);
		internal static readonly symbolic_value bottom = new symbolic_value { is_bottom = true };
		internal bool is_bottom { get; private set; }

		public bool is_unknown { get; }
		public HashSet<string> type_names { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<string> strings { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<MethodDef> methods { get; } = new HashSet<MethodDef>();
		public HashSet<MethodDef> virtual_methods { get; } = new HashSet<MethodDef>();
		public HashSet<reflection_lookup> lookups { get; } = new HashSet<reflection_lookup>();
		public integer_expression? integer { get; private set; }
		public double? floating { get; private set; }
		public static symbolic_value from_floating(double value) => new symbolic_value { floating = value };
		public object? storage { get; private set; }
		public HashSet<object> storage_candidates { get; } = new HashSet<object>();
		public IEnumerable<object> possible_storage => this.storage is null ? this.storage_candidates : new[] { this.storage }.Concat(this.storage_candidates).Distinct();
		public bool is_ambiguous { get; private set; }
		public bool is_boxed { get; private set; }
		public symbolic_value? receiver { get; private set; }
		public bool is_exact_type { get; private set; }
		public bool represents_type { get; private set; }
		public bool may_be_null { get; private set; }
		public static symbolic_value from_reference(string type_name, object identity, bool may_be_null) {
			var value = symbolic_value.from_object(type_name, identity); value.is_exact_type = false; value.may_be_null = may_be_null; value.may_have_unknown_targets = true; return value;
		}
		public bool may_have_unknown_targets { get; private set; }
		public object? opaque_identity { get; private set; }
		public string? unknown_reason { get; private set; }
		public static symbolic_value unknown_because(string reason) => new symbolic_value(true) { unknown_reason = reason };
		internal symbolic_value with_unknown_reason(string? reason) { var value = this.clone(); if (!value.is_unknown) value.unknown_reason = reason; return value; }
		public Dictionary<MethodDef, List<MethodSig>> pointer_signatures { get; } = new Dictionary<MethodDef, List<MethodSig>>();
		public IReadOnlyList<delegate_entry>? delegate_list { get; private set; }
		public method_value_kind method_kind { get; private set; }
		public symbolic_value as_method_kind(method_value_kind kind) { var value = this.clone(); if (!value.is_unknown) value.method_kind = kind; return value; }
		public ImmutableDictionary<string, symbolic_value> members { get; private set; } = ImmutableDictionary<string, symbolic_value>.Empty;
		public int aggregate_depth { get; private set; }
		public symbolic_value with_member(string name, symbolic_value member) {
			if (this.aggregate_depth >= 24 || member.aggregate_depth >= 24) return symbolic_value.unknown_because("aggregate nesting limit reached");
			var value = this.clone(); if (value.is_unknown) return value;
			value.members = value.members.SetItem(name, member);
			value.aggregate_depth = 1 + value.members.Values.Max(item => item.aggregate_depth);
			return value;
		}
		public static bool same_binding(delegate_entry left, delegate_entry right) => ReferenceEquals(left.method, right.method) &&
			(ReferenceEquals(left.receiver, right.receiver) || left.receiver is not null && right.receiver is not null && left.receiver.same_as(right.receiver) ||
				(left.receiver is null || left.receiver.integer?.is_zero == true) && (right.receiver is null || right.receiver.integer?.is_zero == true));
		public symbolic_value with_delegate_list(IReadOnlyList<delegate_entry> entries) {
			var value = this.clone(); if (value.is_unknown) return value;
			value.delegate_list = entries; value.methods.Clear(); value.virtual_methods.Clear();
			foreach (var entry in entries) value.methods.Add(entry.method);
			foreach (var key in value.pointer_signatures.Keys.Where(key => !value.methods.Contains(key)).ToArray()) value.pointer_signatures.Remove(key);
			value.is_ambiguous = false; value.may_have_unknown_targets = false;
			return value;
		}
		public static symbolic_value opaque(object identity) => new symbolic_value { opaque_identity = identity, may_have_unknown_targets = true };
		public HashSet<string> dynamic_names { get; } = new HashSet<string>(StringComparer.Ordinal);
		public static symbolic_value dynamic(IEnumerable<string> names) { var value = new symbolic_value(); value.dynamic_names.UnionWith(names); return value; }
		public symbolic_value with_receiver(symbolic_value? receiver) {
			var value = this.clone(); if (!value.is_unknown) { value.receiver = receiver; if (value.delegate_list is not null) value.delegate_list = value.delegate_list.Select(entry => new delegate_entry(entry.method, receiver)).ToArray(); } return value;
		}
		public symbolic_value non_null() { var value = this.clone(); if (!value.is_unknown) { value.may_be_null = false; if (value.delegate_list is not null && !value.may_have_unknown_targets) value.is_ambiguous = false; } return value; }

		symbolic_value(bool is_unknown = false) => this.is_unknown = is_unknown;
		public static symbolic_value from_integer(integer_expression? expression) => expression is null ? symbolic_value.unknown : new symbolic_value { integer = expression };
		public static symbolic_value from_storage(object storage) => new symbolic_value { storage = storage };
		public static symbolic_value box(symbolic_value value) {
			var result = value.is_unknown ? new symbolic_value() : value.clone(); result.is_boxed = true; return result;
		}
		public static symbolic_value unbox(symbolic_value value) {
			var result = value.clone(); if (!result.is_unknown) result.is_boxed = false; return result;
		}
		public static symbolic_value from_object(string type_name, object storage) {
			var value = symbolic_value.from_type(type_name); value.represents_type = false; value.storage = storage; value.is_exact_type = true; return value;
		}
		public static symbolic_value from_delegate(string type_name, object storage, symbolic_value function) {
			var value = function.clone();
			if (value.is_unknown) return symbolic_value.from_object(type_name, storage);
			value.storage = storage; value.type_names.Add(type_name);
			value.method_kind = method_value_kind.@delegate;
			if (function.methods.Count == 1 && function.virtual_methods.Count == 0 && !function.may_have_unknown_targets) value.delegate_list = new[] { new delegate_entry(function.methods.Single(), function.receiver) };
			return value;
		}

		public static symbolic_value from_type(string type_name) {
			var result = new symbolic_value { represents_type = true };
			if (!string.IsNullOrEmpty(type_name))
				result.type_names.Add(type_name);
			return result;
		}

		public static symbolic_value from_string(string value) {
			var result = new symbolic_value();
			result.strings.Add(value);
			return result;
		}

		public static symbolic_value from_method(MethodDef method, bool is_virtual = false, symbolic_value? receiver = null, MethodSig? signature = null) {
			var result = new symbolic_value();
			result.receiver = receiver;
			result.methods.Add(method);
			result.pointer_signatures[method] = new List<MethodSig> { signature ?? method.MethodSig };
			if (is_virtual)
				result.virtual_methods.Add(method);
			return result;
		}

		public static symbolic_value from_lookup(IEnumerable<string> type_names, IEnumerable<string> method_names) {
			var result = new symbolic_value();
			foreach (string type_name in type_names)
			foreach (string method_name in method_names)
				result.lookups.Add(new reflection_lookup(type_name, method_name));
			return result;
		}

		public symbolic_value clone() {
			if (this.is_bottom) return new symbolic_value { is_bottom = true };
			if (this.is_unknown)
				return this;
			var result = new symbolic_value();
			result.integer = this.integer;
			result.floating = this.floating;
			result.storage = this.storage;
			result.storage_candidates.UnionWith(this.storage_candidates);
			result.is_ambiguous = this.is_ambiguous;
			result.is_boxed = this.is_boxed;
			result.receiver = this.receiver;
			result.is_exact_type = this.is_exact_type;
			result.represents_type = this.represents_type;
			result.may_be_null = this.may_be_null;
			result.may_have_unknown_targets = this.may_have_unknown_targets;
			result.opaque_identity = this.opaque_identity;
			result.unknown_reason = this.unknown_reason;
			result.delegate_list = this.delegate_list;
			result.method_kind = this.method_kind;
			result.members = this.members;
			result.aggregate_depth = this.aggregate_depth;
			foreach (var pair in this.pointer_signatures) result.pointer_signatures[pair.Key] = new List<MethodSig>(pair.Value);
			result.type_names.UnionWith(this.type_names);
			result.strings.UnionWith(this.strings);
			result.methods.UnionWith(this.methods);
			result.virtual_methods.UnionWith(this.virtual_methods);
			result.lookups.UnionWith(this.lookups);
			result.dynamic_names.UnionWith(this.dynamic_names);
			return result;
		}

		public static symbolic_value merge(IEnumerable<symbolic_value> values) {
			var items = values.ToArray();
			if (items.Length == 0) return symbolic_value.unknown;
			if (items.Any(value => value.is_bottom)) items = items.Where(value => !value.is_bottom).ToArray();
			if (items.Length == 0) return symbolic_value.bottom;
			if (items.Length == 0 || items.All(value => value.is_unknown))
				return items.FirstOrDefault(value => value.unknown_reason is not null) ?? symbolic_value.unknown;
			if (items.Any(value => value.is_unknown)) {
				var partial = symbolic_value.merge(items.Where(value => !value.is_unknown));
				partial.is_ambiguous = true; partial.may_have_unknown_targets = true; partial.is_exact_type = false;
				partial.may_be_null = true;
				partial.unknown_reason ??= items.FirstOrDefault(value => value.is_unknown && value.unknown_reason is not null)?.unknown_reason;
				partial.members = partial.members.ToImmutableDictionary(pair => pair.Key, pair => symbolic_value.merge(new[] { pair.Value, symbolic_value.unknown }));
				if (partial.storage is object storage) partial.storage_candidates.Add(storage);
				partial.integer = null; partial.floating = null; partial.storage = null; return partial;
			}
			var result = new symbolic_value();
			foreach (string name in items.SelectMany(item => item.members.Keys).Distinct(StringComparer.Ordinal).Take(64)) {
				var member = symbolic_value.merge(items.Select(item => item.members.TryGetValue(name, out var value) ? value : symbolic_value.unknown));
				result.members = result.members.SetItem(name, member);
			}
			result.aggregate_depth = result.members.Count == 0 ? 0 : 1 + result.members.Values.Max(item => item.aggregate_depth);
			result.is_ambiguous = items.Any(value => value.is_ambiguous) || items.Any(value => !value.same_as(items[0]));
			result.may_have_unknown_targets = items.Any(value => value.may_have_unknown_targets);
			var method_values = items.Where(value => value.methods.Count != 0).ToArray();
			result.method_kind = method_values.All(value => value.method_kind == (method_values.FirstOrDefault()?.method_kind ?? method_value_kind.pointer)) ? method_values.FirstOrDefault()?.method_kind ?? method_value_kind.pointer : method_value_kind.mixed;
			result.unknown_reason = items.Select(value => value.unknown_reason).FirstOrDefault(reason => reason is not null);
			if (items.All(value => Equals(value.opaque_identity, items[0].opaque_identity))) result.opaque_identity = items[0].opaque_identity;
			result.is_boxed = items.All(value => value.is_boxed);
			result.represents_type = items.All(value => value.represents_type);
			result.is_exact_type = items.All(value => value.is_exact_type) && items.All(value => value.type_names.SetEquals(items[0].type_names));
			result.may_be_null = items.Any(value => value.may_be_null || value.integer?.is_zero == true && !value.is_boxed);
			var delegate_values = items.Where(value => value.integer?.is_zero != true || value.is_boxed).ToArray();
			var invocation_list = delegate_values.FirstOrDefault()?.delegate_list;
			if (invocation_list is not null && delegate_values.All(value => value.delegate_list is not null && value.delegate_list.Count == invocation_list.Count && value.delegate_list.Zip(invocation_list, symbolic_value.same_binding).All(equal => equal))) result.delegate_list = invocation_list;
			if (items.All(value => ReferenceEquals(value.receiver, items[0].receiver))) result.receiver = items[0].receiver;
			if (items.All(value => ReferenceEquals(value.integer, items[0].integer))) result.integer = items[0].integer;
			if (items.All(value => symbolic_value.same_float(value.floating, items[0].floating))) result.floating = items[0].floating;
			if (items.Any(value => value.is_boxed) && !result.is_boxed) result.integer = null;
			if (items.All(value => Equals(value.storage, items[0].storage))) result.storage = items[0].storage;
			result.storage_candidates.UnionWith(items.SelectMany(value => value.possible_storage).Take(65));
			if (result.storage_candidates.Count > 64) { limit(result.storage_candidates, 64); result.may_have_unknown_targets = true; result.is_ambiguous = true; result.unknown_reason = "symbolic storage set limit reached"; }
			if (result.storage is not null) result.storage_candidates.Remove(result.storage);
			foreach (var value in items) {
				result.type_names.UnionWith(value.type_names);
				result.strings.UnionWith(value.strings);
				result.methods.UnionWith(value.methods);
				result.virtual_methods.UnionWith(value.virtual_methods);
				result.lookups.UnionWith(value.lookups);
				result.dynamic_names.UnionWith(value.dynamic_names);
				foreach (var pair in value.pointer_signatures) {
					if (!result.pointer_signatures.TryGetValue(pair.Key, out var signatures)) result.pointer_signatures[pair.Key] = signatures = new List<MethodSig>();
					foreach (var signature in pair.Value) if (!signatures.Any(existing => new SigComparer().Equals(existing, signature))) signatures.Add(signature);
				}
			}
			if (result.type_names.Count > 64 || result.strings.Count > 64 || result.methods.Count > 512 || result.lookups.Count > 64) {
				limit(result.type_names, 64); limit(result.strings, 64); limit(result.methods, 512); limit(result.lookups, 64);
				result.virtual_methods.IntersectWith(result.methods);
				foreach (var method in result.pointer_signatures.Keys.Where(method => !result.methods.Contains(method)).ToArray()) result.pointer_signatures.Remove(method);
				result.is_ambiguous = true; result.is_exact_type = false; result.may_have_unknown_targets = true; result.delegate_list = null;
				result.unknown_reason = "symbolic target/value set limit reached";
			}
			return result;

			static void limit<t_value>(HashSet<t_value> values, int maximum) { if (values.Count <= maximum) return; var retained = values.Take(maximum).ToArray(); values.Clear(); values.UnionWith(retained); }
		}

		static bool same_float(double? left, double? right) => left.HasValue == right.HasValue && (!left.HasValue || BitConverter.DoubleToInt64Bits(left.Value) == BitConverter.DoubleToInt64Bits(right!.Value));
		public bool same_as(symbolic_value other) => this.is_bottom == other.is_bottom && this.is_unknown == other.is_unknown &&
			symbolic_value.same_float(this.floating, other.floating) &&
			ReferenceEquals(this.integer, other.integer) && Equals(this.storage, other.storage) && this.storage_candidates.SetEquals(other.storage_candidates) &&
			this.is_ambiguous == other.is_ambiguous &&
			this.is_boxed == other.is_boxed &&
			this.is_exact_type == other.is_exact_type && ReferenceEquals(this.receiver, other.receiver) &&
			this.represents_type == other.represents_type &&
			this.may_be_null == other.may_be_null &&
			this.method_kind == other.method_kind &&
			this.members.Count == other.members.Count && this.members.All(pair => other.members.TryGetValue(pair.Key, out var member) && pair.Value.same_as(member)) &&
			(ReferenceEquals(this.delegate_list, other.delegate_list) || this.delegate_list is not null && other.delegate_list is not null && this.delegate_list.Count == other.delegate_list.Count && this.delegate_list.Zip(other.delegate_list, symbolic_value.same_binding).All(equal => equal)) &&
			this.may_have_unknown_targets == other.may_have_unknown_targets &&
			Equals(this.opaque_identity, other.opaque_identity) && this.pointer_signatures.Count == other.pointer_signatures.Count && this.pointer_signatures.All(pair => other.pointer_signatures.TryGetValue(pair.Key, out var values) && pair.Value.Count == values.Count && pair.Value.All(signature => values.Any(value => new SigComparer().Equals(value, signature)))) &&
			this.type_names.SetEquals(other.type_names) && this.strings.SetEquals(other.strings) &&
			this.methods.SetEquals(other.methods) && this.virtual_methods.SetEquals(other.virtual_methods) &&
			this.lookups.SetEquals(other.lookups) && this.dynamic_names.SetEquals(other.dynamic_names);
	}

	sealed class symbolic_state {
		const int maximum_merged_stack_depth = 64;
		public List<symbolic_value> stack { get; }
		public Dictionary<int, symbolic_value> locals { get; }
		public Dictionary<int, symbolic_value> arguments { get; } = new Dictionary<int, symbolic_value>();
		public ImmutableDictionary<object, symbolic_value> memory { get; private set; } = ImmutableDictionary<object, symbolic_value>.Empty;
		public object frame { get; set; } = new object();
		public object memory_stamp { get; private set; } = new object();
		public bool has_writes { get; private set; }
		public ImmutableHashSet<finally_continuation> finally_targets { get; set; } = ImmutableHashSet<finally_continuation>.Empty;
		public bool exceptional_path { get; set; }
		public bool pending_exception { get; set; }
		public bool path_terminated { get; set; }
		public bool no_return_path { get; set; }
		public bool conservative_context { get; set; }
		public ImmutableHashSet<object> escaped_storage { get; set; } = ImmutableHashSet<object>.Empty;
		public ImmutableHashSet<TypeDef> initialized_types { get; set; } = ImmutableHashSet<TypeDef>.Empty;
		public ImmutableHashSet<TypeDef> initializing_types { get; set; } = ImmutableHashSet<TypeDef>.Empty;
		public ImmutableHashSet<TypeDef> failed_initializers { get; set; } = ImmutableHashSet<TypeDef>.Empty;
		ImmutableHashSet<object> byte_addressed_storage = ImmutableHashSet<object>.Empty;
		public void store(object address, symbolic_value value) {
			if (address is flow_address { kind: "aggregate member" } member) {
				var aggregate = this.load(member.owner);
				this.store(member.owner, aggregate.with_member((string)member.slot, value)); return;
			}
			if (this.memory.TryGetValue(address, out var previous) && previous.same_as(value)) return;
			var root = flow_memory.address_root(address)!;
			if (address is flow_address { kind: "byte offset" }) this.byte_addressed_storage = this.byte_addressed_storage.Add(root);
			if (this.byte_addressed_storage.Contains(root)) foreach (var alias in this.memory.Keys.Where(key => !Equals(key, address) && Equals(flow_memory.address_root(key), root)).ToArray()) this.memory = this.memory.SetItem(alias, symbolic_value.unknown_because("overlapping byte-addressed storage was written"));
			this.memory = this.memory.SetItem(address, value);
			this.memory_stamp = new object(); this.has_writes = true;
		}
		public symbolic_value load(object address) {
			if (address is flow_address { kind: "aggregate member" } member) {
				var aggregate = this.load(member.owner);
				return aggregate.members.TryGetValue((string)member.slot, out var item) ? item : symbolic_value.unknown;
			}
			if (this.memory.TryGetValue(address, out var value)) return value;
			if (address is flow_address slot && Equals(slot.owner, this.frame) && slot.slot is int index) {
				if (slot.kind == "local" && this.locals.TryGetValue(index, out value)) return value;
				if (slot.kind == "argument" && this.arguments.TryGetValue(index, out value)) return value;
			}
			return symbolic_value.unknown;
		}
		public void refine_non_null(symbolic_value tested) {
			if (tested.is_unknown) return;
			var value = tested.non_null();
			for (int index = 0; index < this.stack.Count; index++) if (ReferenceEquals(this.stack[index], tested)) this.stack[index] = value;
			foreach (var key in this.locals.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) this.locals[key] = value;
			foreach (var key in this.arguments.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) this.arguments[key] = value;
			foreach (var key in this.memory.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) this.store(key, value);
		}
		public void import_memory(symbolic_state other) {
			this.initialized_types = other.initialized_types;
			this.initializing_types = other.initializing_types;
			this.failed_initializers = other.failed_initializers;
			this.escaped_storage = other.escaped_storage;
			this.byte_addressed_storage = other.byte_addressed_storage;
			this.memory = other.memory;
			this.memory_stamp = other.memory_stamp; this.has_writes |= other.has_writes;
			this.conservative_context |= other.conservative_context;
		}

		public symbolic_state() {
			this.stack = new List<symbolic_value>();
			this.locals = new Dictionary<int, symbolic_value>();
		}

		symbolic_state(List<symbolic_value> stack, Dictionary<int, symbolic_value> locals) {
			this.stack = stack;
			this.locals = locals;
		}

		public symbolic_state clone() {
			var result = new symbolic_state(new List<symbolic_value>(this.stack), new Dictionary<int, symbolic_value>(this.locals)) { frame = this.frame, memory_stamp = this.memory_stamp, has_writes = this.has_writes, finally_targets = this.finally_targets, exceptional_path = this.exceptional_path, pending_exception = this.pending_exception, path_terminated = this.path_terminated, no_return_path = this.no_return_path, conservative_context = this.conservative_context, escaped_storage = this.escaped_storage };
			foreach (var pair in this.arguments) result.arguments[pair.Key] = pair.Value;
			result.memory = this.memory;
			result.initialized_types = this.initialized_types;
			result.initializing_types = this.initializing_types;
			result.failed_initializers = this.failed_initializers;
			result.byte_addressed_storage = this.byte_addressed_storage;
			return result;
		}

		public bool merge_from(symbolic_state other) {
			bool changed = false;
			if (!this.initialized_types.IsSubsetOf(other.initialized_types)) { this.initialized_types = this.initialized_types.Intersect(other.initialized_types); changed = true; }
			if (!this.initializing_types.IsSubsetOf(other.initializing_types)) { this.initializing_types = this.initializing_types.Intersect(other.initializing_types); changed = true; }
			if (!this.failed_initializers.IsSubsetOf(other.failed_initializers)) { this.failed_initializers = this.failed_initializers.Intersect(other.failed_initializers); changed = true; }
			if (!other.byte_addressed_storage.IsSubsetOf(this.byte_addressed_storage)) { this.byte_addressed_storage = this.byte_addressed_storage.Union(other.byte_addressed_storage); changed = true; }
			if (!other.escaped_storage.IsSubsetOf(this.escaped_storage)) { this.escaped_storage = this.escaped_storage.Union(other.escaped_storage); changed = true; }
			if (other.conservative_context && !this.conservative_context) { this.conservative_context = true; changed = true; }
			if (other.exceptional_path && !this.exceptional_path) { this.exceptional_path = true; changed = true; }
			if (!other.finally_targets.IsSubsetOf(this.finally_targets)) {
				this.finally_targets = this.finally_targets.Union(other.finally_targets); changed = true;
				if (this.finally_targets.Count > 64) throw new InvalidOperationException("finally continuation limit reached");
			}
			foreach (var key in ReferenceEquals(this.memory, other.memory) ? Enumerable.Empty<object>() : this.memory.Keys.Union(other.memory.Keys).ToArray()) {
				var merged = symbolic_value.merge(new[] { this.load(key), other.load(key) });
				if (!this.memory.TryGetValue(key, out var previous) || !previous.same_as(merged)) { this.store(key, merged); changed = true; }
			}
			foreach (int key in this.arguments.Keys.Union(other.arguments.Keys).ToArray()) {
				var left = this.arguments.TryGetValue(key, out var a) ? a : symbolic_value.unknown;
				var right = other.arguments.TryGetValue(key, out var b) ? b : symbolic_value.unknown;
				var merged = symbolic_value.merge(new[] { left, right });
				if (!left.same_as(merged) || !this.arguments.ContainsKey(key)) { this.arguments[key] = merged; changed = true; }
			}
			if (this.stack.Count != other.stack.Count) {
				int count = Math.Min(symbolic_state.maximum_merged_stack_depth, Math.Max(this.stack.Count, other.stack.Count));
				var merged_stack = Enumerable.Repeat(symbolic_value.unknown, count).ToArray();
				if (this.stack.Count != count || this.stack.Where((value, index) => !value.same_as(merged_stack[index])).Any()) {
					this.stack.Clear();
					this.stack.AddRange(merged_stack);
					changed = true;
				}
			}
			else {
				for (int i = 0; i < this.stack.Count; i++) {
					var merged = symbolic_value.merge(new[] { this.stack[i], other.stack[i] });
					if (this.stack[i].same_as(merged))
						continue;
					this.stack[i] = merged;
					changed = true;
				}
			}
			foreach (int index in this.locals.Keys.Union(other.locals.Keys).ToArray()) {
				var left = this.locals.TryGetValue(index, out var left_value) ? left_value : symbolic_value.unknown;
				var right = other.locals.TryGetValue(index, out var right_value) ? right_value : symbolic_value.unknown;
				var merged = symbolic_value.merge(new[] { left, right });
				if (left.same_as(merged) && this.locals.ContainsKey(index))
					continue;
				this.locals[index] = merged;
				changed = true;
			}
			return changed;
		}
	}

	sealed class pointer_flow_catalog {
		readonly Dictionary<FieldDef, symbolic_value> field_values = new Dictionary<FieldDef, symbolic_value>();
		readonly Dictionary<IField, FieldDef?> resolved_fields = new Dictionary<IField, FieldDef?>();
		readonly Dictionary<(object storage, long index), symbolic_value> elements = new Dictionary<(object, long), symbolic_value>();
		readonly Dictionary<object, Dictionary<long, symbolic_value>> array_elements = new Dictionary<object, Dictionary<long, symbolic_value>>();

		public bool merge_field(FieldDef field, symbolic_value value) => pointer_flow_catalog.merge_into(this.field_values, field, value);
		public void set_field(FieldDef field, symbolic_value value) => this.field_values[field] = value;
		public void set_slot(object address, symbolic_value value) {
			this.elements[(storage: address, 0)] = value;
			if (address is flow_address { kind: "element", slot: long index } slot) {
				if (!this.array_elements.TryGetValue(slot.owner, out var entries)) this.array_elements[slot.owner] = entries = new Dictionary<long, symbolic_value>();
				entries[index] = value;
			}
		}

		public symbolic_value field_value(FieldDef? field) => field is not null && this.field_values.TryGetValue(field, out var value)
			? value.clone() : symbolic_value.unknown;

		public bool merge_element(object storage, long index, symbolic_value value) => pointer_flow_catalog.merge_into(this.elements, (storage: storage, index: index), value);
		public symbolic_value element_value(object? storage, integer_expression? index) => storage is not null && index?.is_constant == true &&
			this.elements.TryGetValue((storage: storage, index: index.value), out var value) ? value.clone() : symbolic_value.unknown;
		public symbolic_value slot_value(object? storage) => storage is not null && this.elements.TryGetValue((storage: storage, 0), out var value)
			? value.clone() : symbolic_value.unknown;
		public IEnumerable<(long index, symbolic_value value)> array_elements_value(object storage) => this.array_elements.TryGetValue(storage, out var entries) ? entries.Select(pair => (key: pair.Key, value: pair.Value)) : Enumerable.Empty<(long, symbolic_value)>();

		public FieldDef? resolve_field(IField? reference) {
			if (reference is null)
				return null;
			if (reference is FieldDef definition)
				return definition;
			if (this.resolved_fields.TryGetValue(reference, out var cached))
				return cached;
			FieldDef? result;
			try { result = reference.ResolveFieldDef(); }
			catch (Exception) { result = null; }
			this.resolved_fields[reference] = result;
			return result;
		}

		static bool merge_into<t_key>(Dictionary<t_key, symbolic_value> values, t_key key, symbolic_value value)
			where t_key : notnull {
			if (value.is_unknown || value.methods.Count == 0 && value.storage is null && value.storage_candidates.Count == 0 && value.integer is null && value.floating is null && !value.is_boxed && value.strings.Count == 0 && value.type_names.Count == 0 && value.lookups.Count == 0) {
				if (values.TryGetValue(key, out var previous) && previous.is_unknown) return false;
				values[key] = symbolic_value.unknown; return true;
			}
			var merged = values.TryGetValue(key, out var current)
				? symbolic_value.merge(new[] { current, value }) : value.clone();
			if (values.TryGetValue(key, out current) && current.same_as(merged))
				return false;
			values[key] = merged;
			return true;
		}
	}

	sealed class workspace_call_resolver {
		readonly HashSet<MethodDef> methods;
		readonly Dictionary<string, TypeDef[]> type_map;
		readonly Dictionary<string, MethodDef[]> signature_map;
		readonly Dictionary<string, MethodDef[]> explicit_overrides;
		readonly Dictionary<string, MethodDef[]> method_name_map;
		readonly Dictionary<(TypeDef candidate, TypeDef declaration, bool @interface), bool> relation_cache = new Dictionary<(TypeDef, TypeDef, bool), bool>();
		readonly Dictionary<MethodDef, MethodDef[]> virtual_targets = new Dictionary<MethodDef, MethodDef[]>();
		public workspace_call_resolver(HashSet<MethodDef> methods) {
			this.methods = methods;
			this.type_map = methods.Select(method => method.DeclaringType).Where(type => type is not null).Distinct()
				.GroupBy(call_graph_index.safe_type_name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			this.signature_map = methods.GroupBy(workspace_call_resolver.method_signature_key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			this.explicit_overrides = workspace_call_resolver.build_explicit_override_map(methods);
			this.method_name_map = methods.GroupBy(call_graph_index.safe_method_name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
		}
		public MethodDef[] virtual_targets_value(MethodDef declaration) {
			if (!this.virtual_targets.TryGetValue(declaration, out var targets))
				this.virtual_targets[declaration] = targets = workspace_call_resolver.find_virtual_targets(declaration, this.signature_map, this.explicit_overrides, this.type_map, this.relation_cache).ToArray();
			return targets;
		}
		public static void add_extended_edges(HashSet<MethodDef> methods, IEnumerable<virtual_call_site> virtual_sites,
			IEnumerable<reflection_target> reflection_sites,
			Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> call_sites) =>
			new workspace_call_resolver(methods).add_edges(virtual_sites, reflection_sites, call_sites);
		public void add_edges(IEnumerable<virtual_call_site> virtual_sites, IEnumerable<reflection_target> reflection_sites,
			Dictionary<(MethodDef source, MethodDef target), call_site_accumulator> call_sites) {
			foreach (var site in virtual_sites.GroupBy(site => (source: site.source, declaration: site.declaration, offset: site.offset, operation: site.operation))
				.Select(group => group.First())) {
				foreach (var target in this.virtual_targets_value(site.declaration))
					call_graph_index.add_call_site(call_sites, site.source, target, site.offset, site.operation, true);
			}
			foreach (var site in reflection_sites.GroupBy(site => (source: site.source, offset: site.offset, operation: site.operation,
types: string.Join("\0", site.type_names), names: string.Join("\0", site.method_names)))
				.Select(group => group.First())) {
				foreach (var target in workspace_call_resolver.resolve_reflection_targets(site, this.methods, this.type_map, this.method_name_map))
					call_graph_index.add_call_site(call_sites, site.source, target, site.offset, site.operation, true);
			}
		}

		static Dictionary<string, MethodDef[]> build_explicit_override_map(IEnumerable<MethodDef> methods) {
			var result = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var method in methods) {
				foreach (var method_override in method.Overrides) {
					string key = workspace_call_resolver.method_identity(method_override.MethodDeclaration);
					if (!result.TryGetValue(key, out var values))
						result.Add(key, values = new List<MethodDef>());
					values.Add(method);
				}
			}
			return result.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToArray(), StringComparer.Ordinal);
		}

		static IEnumerable<MethodDef> find_virtual_targets(MethodDef declaration,
			Dictionary<string, MethodDef[]> signature_map, Dictionary<string, MethodDef[]> explicit_overrides,
			Dictionary<string, TypeDef[]> type_map,
			Dictionary<(TypeDef candidate, TypeDef declaration, bool @interface), bool> relation_cache) {
			var result = new HashSet<MethodDef>();
			if (explicit_overrides.TryGetValue(workspace_call_resolver.method_identity(declaration), out var explicit_methods))
				result.UnionWith(explicit_methods.Where(workspace_call_resolver.is_runtime_method));
			if (declaration.DeclaringType is not TypeDef declaring_type)
				return result;
			bool is_interface = declaring_type.IsInterface;
			if (is_interface) {
				TypeDef? resolve_type(ITypeDefOrRef? reference) {
					var matches = workspace_call_resolver.resolve_types(reference, type_map).Take(2).ToArray();
					return matches.Length == 1 ? matches[0] : null;
				}
				foreach (var type in type_map.Values.SelectMany(values => values)) {
					if (!workspace_call_resolver.implements_interface(type, declaring_type, type_map)) continue;
					foreach (var reference in workspace_call_resolver.interface_references(type, declaring_type, type_map)) {
						var member = new MemberRefUser(type.Module, declaration.Name, declaration.MethodSig, reference);
						if (virtual_dispatch_resolver.resolve(declaration, type, resolve_type, member) is MethodDef target && workspace_call_resolver.is_runtime_method(target)) result.Add(target);
					}
				}
				return result;
			}
			if (signature_map.TryGetValue(workspace_call_resolver.method_signature_key(declaration), out var candidates)) {
				foreach (var candidate in candidates) {
					if (ReferenceEquals(candidate, declaration) || !workspace_call_resolver.is_runtime_method(candidate) ||
						candidate.DeclaringType is not TypeDef candidate_type)
						continue;
					if (!is_interface && (!candidate.IsVirtual || candidate.IsNewSlot))
						continue;
					if (candidate.MethodSig.Params.Count != declaration.MethodSig.Params.Count || !candidate.MethodSig.Params.Zip(declaration.MethodSig.Params, (a, b) => new SigComparer().Equals(a, b)).All(equal => equal)) continue;
					var key = (candidate_type: candidate_type, declaring_type: declaring_type, is_interface: is_interface);
					if (!relation_cache.TryGetValue(key, out bool related)) {
						related = is_interface ? workspace_call_resolver.implements_interface(candidate_type, declaring_type, type_map) :
							workspace_call_resolver.derives_from(candidate_type, declaring_type, type_map);
						relation_cache[key] = related;
					}
					if (related) {
						TypeDef? resolve_type(ITypeDefOrRef? reference) {
							var values = workspace_call_resolver.resolve_types(reference, type_map).Take(2).ToArray(); return values.Length == 1 ? values[0] : null;
						}
						if (ReferenceEquals(virtual_dispatch_resolver.resolve(declaration, candidate_type, resolve_type), candidate)) result.Add(candidate);
					}
				}
			}
			return result;
		}
		static IEnumerable<ITypeDefOrRef> interface_references(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var queue = new Queue<ITypeDefOrRef>(); queue.Enqueue(candidate);
			var visited = new HashSet<string>(StringComparer.Ordinal);
			while (queue.Count != 0 && visited.Count < 512) {
				var reference = queue.Dequeue();
				if (!visited.Add(reference.DefinitionAssembly?.FullName + "|" + reference.FullName)) continue;
				foreach (var type in workspace_call_resolver.resolve_types(reference, type_map)) {
					if (workspace_call_resolver.same_type(type, target)) { yield return reference; continue; }
					var arguments = (reference as TypeSpec)?.TypeSig is GenericInstSig instance ? instance.GenericArguments : null;
					ITypeDefOrRef instantiate(ITypeDefOrRef value) => value is TypeSpec specification ? new TypeSpecUser(call_signature.substitute(specification.TypeSig, arguments)) : value;
					foreach (var implementation in type.Interfaces) queue.Enqueue(instantiate(implementation.Interface));
					if (type.BaseType is not null) queue.Enqueue(instantiate(type.BaseType));
				}
			}
		}

		static IEnumerable<MethodDef> resolve_reflection_targets(reflection_target site, HashSet<MethodDef> methods,
			Dictionary<string, TypeDef[]> type_map, Dictionary<string, MethodDef[]> method_name_map) {
			var result = new HashSet<MethodDef>(site.exact_methods.Where(methods.Contains));
			if (site.type_names.Count == 0 || site.method_names.Count == 0)
				return result;
			foreach (string type_name in site.type_names) {
				if (!type_map.TryGetValue(type_name, out var requested_types))
					continue;
				foreach (string method_name in site.method_names) {
					if (!method_name_map.TryGetValue(method_name, out var candidates))
						continue;
					var matches = candidates.Where(candidate => candidate.DeclaringType is TypeDef candidate_type &&
						requested_types.Any(requested => ReferenceEquals(candidate_type, requested) ||
							workspace_call_resolver.derives_from(requested, candidate_type, type_map))).Take(2).ToArray();
					if (matches.Length == 1) result.Add(matches[0]);
				}
			}
			return result;
		}

		static bool implements_interface(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>();
			queue.Enqueue(candidate);
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				foreach (var implementation in current.Interfaces) {
					foreach (var resolved in workspace_call_resolver.resolve_types(implementation.Interface, type_map)) {
						if (workspace_call_resolver.same_type(resolved, target))
							return true;
						queue.Enqueue(resolved);
					}
				}
				foreach (var base_type in workspace_call_resolver.resolve_types(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static bool derives_from(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>(workspace_call_resolver.resolve_types(candidate.BaseType, type_map));
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				if (workspace_call_resolver.same_type(current, target))
					return true;
				foreach (var base_type in workspace_call_resolver.resolve_types(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static IEnumerable<TypeDef> resolve_types(ITypeDefOrRef? reference, Dictionary<string, TypeDef[]> type_map) {
			if (reference is null)
				yield break;
			if (reference is TypeSpec specification && specification.TypeSig is GenericInstSig instance) reference = instance.GenericType.TypeDefOrRef;
			if (reference is TypeDef definition) {
				yield return definition;
				yield break;
			}
			TypeDef? resolved;
			try { resolved = reference.ResolveTypeDef(); }
			catch (Exception) { resolved = null; }
			if (resolved is not null) {
				yield return resolved;
				yield break;
			}
			if (type_map.TryGetValue(call_graph_index.safe_type_name(reference), out var candidates))
				foreach (var candidate in candidates)
					if (reference.DefinitionAssembly is null || StringComparer.OrdinalIgnoreCase.Equals(reference.DefinitionAssembly.FullName, candidate.DefinitionAssembly?.FullName)) yield return candidate;
		}

		static bool same_type(TypeDef left, TypeDef right) => ReferenceEquals(left, right) ||
			StringComparer.Ordinal.Equals(call_graph_index.safe_type_name(left), call_graph_index.safe_type_name(right)) &&
			StringComparer.Ordinal.Equals(left.DefinitionAssembly?.FullName, right.DefinitionAssembly?.FullName);

		static bool is_runtime_method(MethodDef method) {
			try { return !method.IsStatic && !method.IsAbstract && method.HasBody; }
			catch (Exception) { return false; }
		}

		static string method_signature_key(MethodDef method) {
			try {
				return call_graph_index.safe_method_name(method) + "`" + method.GenericParameters.Count + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return call_graph_index.safe_method_name(method); }
		}

		static string method_identity(IMethod method) {
			try {
				return method.DeclaringType?.DefinitionAssembly?.FullName + "|" + call_graph_index.safe_type_name(method.DeclaringType) + "::" +
					(method.Name?.ToString() ?? string.Empty) + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return method.FullName ?? string.Empty; }
		}
	}
}
