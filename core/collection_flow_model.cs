using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed class collection_flow_model {
		const int maximum_elements = 512;
		readonly integer_expression.factory integers = new integer_expression.factory();
		symbolic_value number(long value) => symbolic_value.from_integer(this.integers.constant(value));
		static flow_address slot(string kind, object owner, object index) => new flow_address("collection " + kind, owner, index);
		static bool is_list(string name) => name.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal);
		static bool is_enumerator(string name) => collection_flow_model.is_list(name) && (name.Contains("/Enumerator", StringComparison.Ordinal) || name.Contains("+Enumerator", StringComparison.Ordinal));
		static bool is_tuple(string name) => name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || name.StartsWith("System.Tuple`", StringComparison.Ordinal);
		static symbolic_value read_member(symbolic_value instance, string member) => instance.members.TryGetValue(member, out var value) ? value : symbolic_value.unknown;
		static long? constant(symbolic_value value) => value.integer?.is_constant == true ? value.integer.value : null;
		public bool try_evaluate(IMethod reference, symbolic_value[] arguments, symbolic_state state, object allocation, bool constructor, out symbolic_value result) {
			result = symbolic_value.unknown;
			string type = call_graph_index.safe_type_name(reference.DeclaringType), name = reference.Name;
			if (!collection_flow_model.is_tuple(type) && type != "System.Tuple" && type != "System.ValueTuple" && !type.StartsWith("System.Collections.", StringComparison.Ordinal) && type != "System.IDisposable") return false;
			bool has_this = reference.MethodSig?.HasThis == true && !constructor;
			var raw = has_this && arguments.Length != 0 ? arguments[0] : symbolic_value.unknown;
			var instance = flow_memory.read_object(state, raw);
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			if (collection_flow_model.is_tuple(type) || type.StartsWith("System.Collections.Generic.KeyValuePair`2", StringComparison.Ordinal)) {
				if (name == ".ctor") {
					var tuple = symbolic_value.from_object(type, allocation);
					for (int index = 0; index < values.Length && index < 8; index++) tuple = tuple.with_member(type.StartsWith("System.Collections.Generic.KeyValuePair`2", StringComparison.Ordinal) ? index == 0 ? "Key" : "Value" : index == 7 ? "Rest" : "Item" + (index + 1), values[index]);
					if (!constructor && raw.storage is object address) state.store(address, tuple);
					result = tuple; return true;
				}
				if (name.StartsWith("get_", StringComparison.Ordinal)) { result = collection_flow_model.read_member(instance, name.Substring(4)); return true; }
			}
			if ((type == "System.ValueTuple" || type == "System.Tuple") && name == "Create") {
				result = symbolic_value.from_object(type + "`" + values.Length, allocation);
				for (int index = 0; index < values.Length && index < 8; index++) result = result.with_member(index == 7 ? "Rest" : "Item" + (index + 1), values[index]);
				return true;
			}
			bool enumerator = collection_flow_model.is_enumerator(type) || instance.members.ContainsKey("$collection");
			if (enumerator) {
				var collection = collection_flow_model.read_member(instance, "$collection");
				if (collection.storage is not object owner) return false;
				var position = collection_flow_model.constant(collection_flow_model.read_member(instance, "$position"));
				var count = collection_flow_model.constant(state.load(collection_flow_model.slot("count", owner, 0)));
				if (name == "MoveNext") {
					long? next = position.HasValue && position < collection_flow_model.maximum_elements ? position + 1 : null;
					var updated = instance.with_member("$position", next.HasValue ? this.number(next.Value) : symbolic_value.unknown);
					if (raw.storage is object address) state.store(address, updated);
					result = next.HasValue && count.HasValue ? this.number(next < count ? 1 : 0) : symbolic_value.unknown;
					return true;
				}
				if (name is "get_Current" or "System.Collections.IEnumerator.get_Current") { result = this.element(collection, position, state); return true; }
				if (name == "Dispose") return true;
			}
			if (!collection_flow_model.is_list(type) && !instance.members.ContainsKey("$list")) return false;
			if (constructor) {
				result = symbolic_value.from_object(type, allocation).with_member("$list", this.number(1));
				state.store(collection_flow_model.slot("count", allocation, 0), this.number(0));
				if (values.Length != 0 && reference.MethodSig?.Params.FirstOrDefault()?.ElementType != ElementType.I4) {
					var source = values[0]; var source_count = this.count(source, state);
					if (source_count.HasValue && source_count >= 0 && source_count <= collection_flow_model.maximum_elements) {
						for (long index = 0; index < source_count; index++) state.store(collection_flow_model.slot("item", allocation, index), this.element(source, index, state));
						state.store(collection_flow_model.slot("count", allocation, 0), this.number(source_count.Value));
					}
					else {
						state.store(collection_flow_model.slot("count", allocation, 0), symbolic_value.unknown_because("collection source contents are unknown"));
						state.store(collection_flow_model.slot("candidates", allocation, 0), this.element(source, null, state));
					}
				}
				return true;
			}
			if (instance.storage is not object storage) return false;
			long? length = this.count(instance, state);
			if (name == "get_Count") { result = state.load(collection_flow_model.slot("count", storage, 0)); return true; }
			if (name is "get_Capacity" or "get_IsReadOnly" or "Contains" or "IndexOf" or "LastIndexOf") return true;
			if (name == "Add" && values.Length == 1) {
				if (length.HasValue && length >= 0 && length < collection_flow_model.maximum_elements) {
					state.store(collection_flow_model.slot("item", storage, length.Value), values[0]); state.store(collection_flow_model.slot("count", storage, 0), this.number(length.Value + 1));
				}
				else {
					state.store(collection_flow_model.slot("candidates", storage, 0), symbolic_value.merge(new[] { state.load(collection_flow_model.slot("candidates", storage, 0)), values[0] }));
					state.store(collection_flow_model.slot("count", storage, 0), symbolic_value.unknown_because("collection size or insertion position is unknown"));
				}
				return true;
			}
			if (name == "get_Item" && values.Length == 1) { result = this.element(instance, collection_flow_model.constant(values[0]), state); return true; }
			if (name == "AddRange" && values.Length == 1) {
				var source_count = this.count(values[0], state);
				if (length.HasValue && source_count.HasValue && length >= 0 && source_count >= 0 && length + source_count <= collection_flow_model.maximum_elements) {
					var additions = Enumerable.Range(0, (int)source_count.Value).Select(index => this.element(values[0], index, state)).ToArray();
					for (int index = 0; index < additions.Length; index++) state.store(collection_flow_model.slot("item", storage, length.Value + index), additions[index]);
					state.store(collection_flow_model.slot("count", storage, 0), this.number(length.Value + source_count.Value));
				}
				else collection_flow_model.invalidate(storage, state, "collection range source or size is unknown");
				return true;
			}
			if (name == "set_Item" && values.Length == 2) {
				var position = collection_flow_model.constant(values[0]);
				if (position.HasValue && position >= 0 && position < collection_flow_model.maximum_elements && (!length.HasValue || position < length)) state.store(collection_flow_model.slot("item", storage, position.Value), values[1]);
				else if (!position.HasValue) foreach (var key in collection_flow_model.item_keys(storage, state)) state.store(key, symbolic_value.merge(new[] { state.load(key), values[1] }));
				else if (!length.HasValue) collection_flow_model.invalidate(storage, state, "collection replacement position is unknown");
				return true;
			}
			if (name == "Clear") { foreach (var key in collection_flow_model.item_keys(storage, state)) state.store(key, symbolic_value.unknown); state.store(collection_flow_model.slot("candidates", storage, 0), symbolic_value.unknown); state.store(collection_flow_model.slot("count", storage, 0), this.number(0)); return true; }
			if (name == "Reverse" && values.Length == 0 && length.HasValue && length >= 0 && length <= collection_flow_model.maximum_elements) {
				var reversed = Enumerable.Range(0, (int)length.Value).Select(index => this.element(instance, length.Value - 1 - index, state)).ToArray();
				for (int index = 0; index < reversed.Length; index++) state.store(collection_flow_model.slot("item", storage, (long)index), reversed[index]);
				return true;
			}
			if (name == "RemoveAt" && values.Length == 1 && collection_flow_model.constant(values[0]) is long removed && length.HasValue && removed >= 0 && removed < length && length <= collection_flow_model.maximum_elements) {
				for (long index = removed; index < length - 1; index++) state.store(collection_flow_model.slot("item", storage, index), state.load(collection_flow_model.slot("item", storage, index + 1)));
				state.store(collection_flow_model.slot("item", storage, length.Value - 1), symbolic_value.unknown); state.store(collection_flow_model.slot("count", storage, 0), this.number(length.Value - 1)); return true;
			}
			if (name == "GetEnumerator") {
				result = symbolic_value.from_object(type + "/Enumerator", allocation).with_member("$collection", instance).with_member("$position", this.number(-1)); return true;
			}
			if (name == "ToArray" && length.HasValue && length >= 0 && length <= collection_flow_model.maximum_elements) {
				result = symbolic_value.from_object("System.Object[]", allocation); state.store(new flow_address("length", allocation, 0), this.number(length.Value));
				for (long index = 0; index < length; index++) state.store(new flow_address("element", allocation, index), this.element(instance, index, state));
				return true;
			}
			collection_flow_model.invalidate(storage, state, "collection operation is not modeled: " + name);
			result = symbolic_value.unknown_because("collection operation is not modeled: " + name);
			return true;
		}
		long? count(symbolic_value collection, symbolic_state state) => collection.storage is object owner ? collection_flow_model.constant(state.load(collection.type_names.Any(name => name.EndsWith("[]", StringComparison.Ordinal)) ? new flow_address("length", owner, 0) : collection_flow_model.slot("count", owner, 0))) : null;
		symbolic_value element(symbolic_value collection, long? index, symbolic_state state) {
			if (collection.storage is not object owner) return symbolic_value.unknown;
			bool array = collection.type_names.Any(name => name.EndsWith("[]", StringComparison.Ordinal));
			long? count = this.count(collection, state);
			if (index.HasValue && count.HasValue && index >= 0 && index < count) return state.load(array ? new flow_address("element", owner, index.Value) : collection_flow_model.slot("item", owner, index.Value));
			if (index.HasValue && count.HasValue) return symbolic_value.unknown_because("collection access is out of range");
			var items = state.memory.Where(pair => pair.Key is flow_address slot && slot.kind == (array ? "element" : "collection item") && Equals(slot.owner, owner) && slot.slot is long position && position >= 0 && (!count.HasValue || position < count)).Take(collection_flow_model.maximum_elements).Select(pair => pair.Value).ToArray();
			return symbolic_value.merge(!count.HasValue ? items.Append(state.load(collection_flow_model.slot("candidates", owner, 0))).Append(symbolic_value.unknown_because("collection contents are incomplete")) : items);
		}
		static object[] item_keys(object owner, symbolic_state state) => state.memory.Keys.Where(key => key is flow_address slot && slot.kind == "collection item" && Equals(slot.owner, owner)).ToArray();
		static void invalidate(object owner, symbolic_state state, string reason) {
			var unknown = symbolic_value.unknown_because(reason);
			foreach (var key in collection_flow_model.item_keys(owner, state)) state.store(key, unknown);
			state.store(collection_flow_model.slot("count", owner, 0), unknown);
		}
	}
}
