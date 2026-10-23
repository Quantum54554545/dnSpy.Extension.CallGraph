using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Core {
	sealed class CollectionFlowModel {
		const int MaximumElements = 512;
		readonly IntegerExpression.Factory integers = new IntegerExpression.Factory();
		SymbolicValue Number(long value) => SymbolicValue.FromInteger(integers.Constant(value));
		static FlowAddress Slot(string kind, object owner, object index) => new FlowAddress("collection " + kind, owner, index);
		static bool IsList(string name) => name.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal);
		static bool IsEnumerator(string name) => IsList(name) && (name.Contains("/Enumerator", StringComparison.Ordinal) || name.Contains("+Enumerator", StringComparison.Ordinal));
		static bool IsTuple(string name) => name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || name.StartsWith("System.Tuple`", StringComparison.Ordinal);
		static SymbolicValue ReadMember(SymbolicValue instance, string member) => instance.Members.TryGetValue(member, out var value) ? value : SymbolicValue.Unknown;
		static long? Constant(SymbolicValue value) => value.Integer?.IsConstant == true ? value.Integer.Value : null;
		public bool TryEvaluate(IMethod reference, SymbolicValue[] arguments, SymbolicState state, object allocation, bool constructor, out SymbolicValue result) {
			result = SymbolicValue.Unknown;
			string type = CallGraphIndex.SafeTypeName(reference.DeclaringType), name = reference.Name;
			if (!IsTuple(type) && type != "System.Tuple" && type != "System.ValueTuple" && !type.StartsWith("System.Collections.", StringComparison.Ordinal) && type != "System.IDisposable") return false;
			bool has_this = reference.MethodSig?.HasThis == true && !constructor;
			var raw = has_this && arguments.Length != 0 ? arguments[0] : SymbolicValue.Unknown;
			var instance = FlowMemory.ReadObject(state, raw);
			var values = has_this ? arguments.Skip(1).ToArray() : arguments;
			if (IsTuple(type) || type.StartsWith("System.Collections.Generic.KeyValuePair`2", StringComparison.Ordinal)) {
				if (name == ".ctor") {
					var tuple = SymbolicValue.FromObject(type, allocation);
					for (int index = 0; index < values.Length && index < 8; index++) tuple = tuple.WithMember(type.StartsWith("System.Collections.Generic.KeyValuePair`2", StringComparison.Ordinal) ? index == 0 ? "Key" : "Value" : index == 7 ? "Rest" : "Item" + (index + 1), values[index]);
					if (!constructor && raw.Storage is object address) state.Store(address, tuple);
					result = tuple; return true;
				}
				if (name.StartsWith("get_", StringComparison.Ordinal)) { result = ReadMember(instance, name.Substring(4)); return true; }
			}
			if ((type == "System.ValueTuple" || type == "System.Tuple") && name == "Create") {
				result = SymbolicValue.FromObject(type + "`" + values.Length, allocation);
				for (int index = 0; index < values.Length && index < 8; index++) result = result.WithMember(index == 7 ? "Rest" : "Item" + (index + 1), values[index]);
				return true;
			}
			bool enumerator = IsEnumerator(type) || instance.Members.ContainsKey("$collection");
			if (enumerator) {
				var collection = ReadMember(instance, "$collection");
				if (collection.Storage is not object owner) return false;
				var position = Constant(ReadMember(instance, "$position"));
				var count = Constant(state.Load(Slot("count", owner, 0)));
				if (name == "MoveNext") {
					long? next = position.HasValue && position < MaximumElements ? position + 1 : null;
					var updated = instance.WithMember("$position", next.HasValue ? Number(next.Value) : SymbolicValue.Unknown);
					if (raw.Storage is object address) state.Store(address, updated);
					result = next.HasValue && count.HasValue ? Number(next < count ? 1 : 0) : SymbolicValue.Unknown;
					return true;
				}
				if (name is "get_Current" or "System.Collections.IEnumerator.get_Current") { result = Element(collection, position, state); return true; }
				if (name == "Dispose") return true;
			}
			if (!IsList(type) && !instance.Members.ContainsKey("$list")) return false;
			if (constructor) {
				result = SymbolicValue.FromObject(type, allocation).WithMember("$list", Number(1));
				state.Store(Slot("count", allocation, 0), Number(0));
				if (values.Length != 0 && reference.MethodSig?.Params.FirstOrDefault()?.ElementType != ElementType.I4) {
					var source = values[0]; var source_count = Count(source, state);
					if (source_count.HasValue && source_count >= 0 && source_count <= MaximumElements) {
						for (long index = 0; index < source_count; index++) state.Store(Slot("item", allocation, index), Element(source, index, state));
						state.Store(Slot("count", allocation, 0), Number(source_count.Value));
					}
					else {
						state.Store(Slot("count", allocation, 0), SymbolicValue.UnknownBecause("collection source contents are unknown"));
						state.Store(Slot("candidates", allocation, 0), Element(source, null, state));
					}
				}
				return true;
			}
			if (instance.Storage is not object storage) return false;
			long? length = Count(instance, state);
			if (name == "get_Count") { result = state.Load(Slot("count", storage, 0)); return true; }
			if (name is "get_Capacity" or "get_IsReadOnly" or "Contains" or "IndexOf" or "LastIndexOf") return true;
			if (name == "Add" && values.Length == 1) {
				if (length.HasValue && length >= 0 && length < MaximumElements) {
					state.Store(Slot("item", storage, length.Value), values[0]); state.Store(Slot("count", storage, 0), Number(length.Value + 1));
				}
				else {
					state.Store(Slot("candidates", storage, 0), SymbolicValue.Merge(new[] { state.Load(Slot("candidates", storage, 0)), values[0] }));
					state.Store(Slot("count", storage, 0), SymbolicValue.UnknownBecause("collection size or insertion position is unknown"));
				}
				return true;
			}
			if (name == "get_Item" && values.Length == 1) { result = Element(instance, Constant(values[0]), state); return true; }
			if (name == "AddRange" && values.Length == 1) {
				var source_count = Count(values[0], state);
				if (length.HasValue && source_count.HasValue && length >= 0 && source_count >= 0 && length + source_count <= MaximumElements) {
					var additions = Enumerable.Range(0, (int)source_count.Value).Select(index => Element(values[0], index, state)).ToArray();
					for (int index = 0; index < additions.Length; index++) state.Store(Slot("item", storage, length.Value + index), additions[index]);
					state.Store(Slot("count", storage, 0), Number(length.Value + source_count.Value));
				}
				else Invalidate(storage, state, "collection range source or size is unknown");
				return true;
			}
			if (name == "set_Item" && values.Length == 2) {
				var position = Constant(values[0]);
				if (position.HasValue && position >= 0 && position < MaximumElements && (!length.HasValue || position < length)) state.Store(Slot("item", storage, position.Value), values[1]);
				else if (!position.HasValue) foreach (var key in ItemKeys(storage, state)) state.Store(key, SymbolicValue.Merge(new[] { state.Load(key), values[1] }));
				else if (!length.HasValue) Invalidate(storage, state, "collection replacement position is unknown");
				return true;
			}
			if (name == "Clear") { foreach (var key in ItemKeys(storage, state)) state.Store(key, SymbolicValue.Unknown); state.Store(Slot("candidates", storage, 0), SymbolicValue.Unknown); state.Store(Slot("count", storage, 0), Number(0)); return true; }
			if (name == "Reverse" && values.Length == 0 && length.HasValue && length >= 0 && length <= MaximumElements) {
				var reversed = Enumerable.Range(0, (int)length.Value).Select(index => Element(instance, length.Value - 1 - index, state)).ToArray();
				for (int index = 0; index < reversed.Length; index++) state.Store(Slot("item", storage, (long)index), reversed[index]);
				return true;
			}
			if (name == "RemoveAt" && values.Length == 1 && Constant(values[0]) is long removed && length.HasValue && removed >= 0 && removed < length && length <= MaximumElements) {
				for (long index = removed; index < length - 1; index++) state.Store(Slot("item", storage, index), state.Load(Slot("item", storage, index + 1)));
				state.Store(Slot("item", storage, length.Value - 1), SymbolicValue.Unknown); state.Store(Slot("count", storage, 0), Number(length.Value - 1)); return true;
			}
			if (name == "GetEnumerator") {
				result = SymbolicValue.FromObject(type + "/Enumerator", allocation).WithMember("$collection", instance).WithMember("$position", Number(-1)); return true;
			}
			if (name == "ToArray" && length.HasValue && length >= 0 && length <= MaximumElements) {
				result = SymbolicValue.FromObject("System.Object[]", allocation); state.Store(new FlowAddress("length", allocation, 0), Number(length.Value));
				for (long index = 0; index < length; index++) state.Store(new FlowAddress("element", allocation, index), Element(instance, index, state));
				return true;
			}
			Invalidate(storage, state, "collection operation is not modeled: " + name);
			result = SymbolicValue.UnknownBecause("collection operation is not modeled: " + name);
			return true;
		}
		long? Count(SymbolicValue collection, SymbolicState state) => collection.Storage is object owner ? Constant(state.Load(collection.TypeNames.Any(name => name.EndsWith("[]", StringComparison.Ordinal)) ? new FlowAddress("length", owner, 0) : Slot("count", owner, 0))) : null;
		SymbolicValue Element(SymbolicValue collection, long? index, SymbolicState state) {
			if (collection.Storage is not object owner) return SymbolicValue.Unknown;
			bool array = collection.TypeNames.Any(name => name.EndsWith("[]", StringComparison.Ordinal));
			long? count = Count(collection, state);
			if (index.HasValue && count.HasValue && index >= 0 && index < count) return state.Load(array ? new FlowAddress("element", owner, index.Value) : Slot("item", owner, index.Value));
			if (index.HasValue && count.HasValue) return SymbolicValue.UnknownBecause("collection access is out of range");
			var items = state.Memory.Where(pair => pair.Key is FlowAddress slot && slot.Kind == (array ? "element" : "collection item") && Equals(slot.Owner, owner) && slot.Slot is long position && position >= 0 && (!count.HasValue || position < count)).Take(MaximumElements).Select(pair => pair.Value).ToArray();
			return SymbolicValue.Merge(!count.HasValue ? items.Append(state.Load(Slot("candidates", owner, 0))).Append(SymbolicValue.UnknownBecause("collection contents are incomplete")) : items);
		}
		static object[] ItemKeys(object owner, SymbolicState state) => state.Memory.Keys.Where(key => key is FlowAddress slot && slot.Kind == "collection item" && Equals(slot.Owner, owner)).ToArray();
		static void Invalidate(object owner, SymbolicState state, string reason) {
			var unknown = SymbolicValue.UnknownBecause(reason);
			foreach (var key in ItemKeys(owner, state)) state.Store(key, unknown);
			state.Store(Slot("count", owner, 0), unknown);
		}
	}
}
