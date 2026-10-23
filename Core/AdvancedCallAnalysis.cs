using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record DelegateEntry(MethodDef Method, SymbolicValue? Receiver);
	sealed record FinallyContinuation(int Position, FinallyContinuation? Next);
	enum MethodValueKind { Pointer, Handle, Reflection, Delegate, Mixed }
	sealed class ReflectionLookup : IEquatable<ReflectionLookup> {
		public string TypeName { get; }
		public string MethodName { get; }

		public ReflectionLookup(string type_name, string method_name) {
			TypeName = type_name;
			MethodName = method_name;
		}

		public bool Equals(ReflectionLookup? other) => other is not null &&
			StringComparer.Ordinal.Equals(TypeName, other.TypeName) &&
			StringComparer.Ordinal.Equals(MethodName, other.MethodName);
		public override bool Equals(object? obj) => Equals(obj as ReflectionLookup);
		public override int GetHashCode() => HashCode.Combine(
			StringComparer.Ordinal.GetHashCode(TypeName), StringComparer.Ordinal.GetHashCode(MethodName));
	}

	sealed class SymbolicValue {
		public static readonly SymbolicValue Unknown = new SymbolicValue(true);

		public bool IsUnknown { get; }
		public HashSet<string> TypeNames { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<string> Strings { get; } = new HashSet<string>(StringComparer.Ordinal);
		public HashSet<MethodDef> Methods { get; } = new HashSet<MethodDef>();
		public HashSet<MethodDef> VirtualMethods { get; } = new HashSet<MethodDef>();
		public HashSet<ReflectionLookup> Lookups { get; } = new HashSet<ReflectionLookup>();
		public IntegerExpression? Integer { get; private set; }
		public double? Floating { get; private set; }
		public static SymbolicValue FromFloating(double value) => new SymbolicValue { Floating = value };
		public object? Storage { get; private set; }
		public HashSet<object> StorageCandidates { get; } = new HashSet<object>();
		public IEnumerable<object> PossibleStorage => Storage is null ? StorageCandidates : new[] { Storage }.Concat(StorageCandidates).Distinct();
		public bool IsAmbiguous { get; private set; }
		public bool IsBoxed { get; private set; }
		public SymbolicValue? Receiver { get; private set; }
		public bool IsExactType { get; private set; }
		public bool RepresentsType { get; private set; }
		public bool MayBeNull { get; private set; }
		public static SymbolicValue FromReference(string type_name, object identity, bool may_be_null) {
			var value = FromObject(type_name, identity); value.IsExactType = false; value.MayBeNull = may_be_null; value.MayHaveUnknownTargets = true; return value;
		}
		public bool MayHaveUnknownTargets { get; private set; }
		public object? OpaqueIdentity { get; private set; }
		public string? UnknownReason { get; private set; }
		public static SymbolicValue UnknownBecause(string reason) => new SymbolicValue(true) { UnknownReason = reason };
		public Dictionary<MethodDef, List<MethodSig>> PointerSignatures { get; } = new Dictionary<MethodDef, List<MethodSig>>();
		public IReadOnlyList<DelegateEntry>? DelegateList { get; private set; }
		public MethodValueKind MethodKind { get; private set; }
		public SymbolicValue AsMethodKind(MethodValueKind kind) { var value = Clone(); if (!value.IsUnknown) value.MethodKind = kind; return value; }
		public ImmutableDictionary<string, SymbolicValue> Members { get; private set; } = ImmutableDictionary<string, SymbolicValue>.Empty;
		public int AggregateDepth { get; private set; }
		public SymbolicValue WithMember(string name, SymbolicValue member) {
			if (AggregateDepth >= 24 || member.AggregateDepth >= 24) return UnknownBecause("aggregate nesting limit reached");
			var value = Clone(); if (value.IsUnknown) return value;
			value.Members = value.Members.SetItem(name, member);
			value.AggregateDepth = 1 + value.Members.Values.Max(item => item.AggregateDepth);
			return value;
		}
		public static bool SameBinding(DelegateEntry left, DelegateEntry right) => ReferenceEquals(left.Method, right.Method) &&
			(ReferenceEquals(left.Receiver, right.Receiver) || left.Receiver is not null && right.Receiver is not null && left.Receiver.SameAs(right.Receiver) ||
				(left.Receiver is null || left.Receiver.Integer?.IsZero == true) && (right.Receiver is null || right.Receiver.Integer?.IsZero == true));
		public SymbolicValue WithDelegateList(IReadOnlyList<DelegateEntry> entries) {
			var value = Clone(); if (value.IsUnknown) return value;
			value.DelegateList = entries; value.Methods.Clear(); value.VirtualMethods.Clear();
			foreach (var entry in entries) value.Methods.Add(entry.Method);
			foreach (var key in value.PointerSignatures.Keys.Where(key => !value.Methods.Contains(key)).ToArray()) value.PointerSignatures.Remove(key);
			value.IsAmbiguous = false; value.MayHaveUnknownTargets = false;
			return value;
		}
		public static SymbolicValue Opaque(object identity) => new SymbolicValue { OpaqueIdentity = identity, MayHaveUnknownTargets = true };
		public HashSet<string> DynamicNames { get; } = new HashSet<string>(StringComparer.Ordinal);
		public static SymbolicValue Dynamic(IEnumerable<string> names) { var value = new SymbolicValue(); value.DynamicNames.UnionWith(names); return value; }
		public SymbolicValue WithReceiver(SymbolicValue? receiver) {
			var value = Clone(); if (!value.IsUnknown) { value.Receiver = receiver; if (value.DelegateList is not null) value.DelegateList = value.DelegateList.Select(entry => new DelegateEntry(entry.Method, receiver)).ToArray(); } return value;
		}
		public SymbolicValue NonNull() { var value = Clone(); if (!value.IsUnknown) { value.MayBeNull = false; if (value.DelegateList is not null && !value.MayHaveUnknownTargets) value.IsAmbiguous = false; } return value; }

		SymbolicValue(bool is_unknown = false) => IsUnknown = is_unknown;
		public static SymbolicValue FromInteger(IntegerExpression? expression) => expression is null ? Unknown : new SymbolicValue { Integer = expression };
		public static SymbolicValue FromStorage(object storage) => new SymbolicValue { Storage = storage };
		public static SymbolicValue Box(SymbolicValue value) {
			var result = value.IsUnknown ? new SymbolicValue() : value.Clone(); result.IsBoxed = true; return result;
		}
		public static SymbolicValue Unbox(SymbolicValue value) {
			var result = value.Clone(); if (!result.IsUnknown) result.IsBoxed = false; return result;
		}
		public static SymbolicValue FromObject(string type_name, object storage) {
			var value = FromType(type_name); value.RepresentsType = false; value.Storage = storage; value.IsExactType = true; return value;
		}
		public static SymbolicValue FromDelegate(string type_name, object storage, SymbolicValue function) {
			var value = function.Clone();
			if (value.IsUnknown) return FromObject(type_name, storage);
			value.Storage = storage; value.TypeNames.Add(type_name);
			value.MethodKind = MethodValueKind.Delegate;
			if (function.Methods.Count == 1 && function.VirtualMethods.Count == 0 && !function.MayHaveUnknownTargets) value.DelegateList = new[] { new DelegateEntry(function.Methods.Single(), function.Receiver) };
			return value;
		}

		public static SymbolicValue FromType(string type_name) {
			var result = new SymbolicValue { RepresentsType = true };
			if (!string.IsNullOrEmpty(type_name))
				result.TypeNames.Add(type_name);
			return result;
		}

		public static SymbolicValue FromString(string value) {
			var result = new SymbolicValue();
			result.Strings.Add(value);
			return result;
		}

		public static SymbolicValue FromMethod(MethodDef method, bool is_virtual = false, SymbolicValue? receiver = null, MethodSig? signature = null) {
			var result = new SymbolicValue();
			result.Receiver = receiver;
			result.Methods.Add(method);
			result.PointerSignatures[method] = new List<MethodSig> { signature ?? method.MethodSig };
			if (is_virtual)
				result.VirtualMethods.Add(method);
			return result;
		}

		public static SymbolicValue FromLookup(IEnumerable<string> type_names, IEnumerable<string> method_names) {
			var result = new SymbolicValue();
			foreach (string type_name in type_names)
			foreach (string method_name in method_names)
				result.Lookups.Add(new ReflectionLookup(type_name, method_name));
			return result;
		}

		public SymbolicValue Clone() {
			if (IsUnknown)
				return this;
			var result = new SymbolicValue();
			result.Integer = Integer;
			result.Floating = Floating;
			result.Storage = Storage;
			result.StorageCandidates.UnionWith(StorageCandidates);
			result.IsAmbiguous = IsAmbiguous;
			result.IsBoxed = IsBoxed;
			result.Receiver = Receiver;
			result.IsExactType = IsExactType;
			result.RepresentsType = RepresentsType;
			result.MayBeNull = MayBeNull;
			result.MayHaveUnknownTargets = MayHaveUnknownTargets;
			result.OpaqueIdentity = OpaqueIdentity;
			result.UnknownReason = UnknownReason;
			result.DelegateList = DelegateList;
			result.MethodKind = MethodKind;
			result.Members = Members;
			result.AggregateDepth = AggregateDepth;
			foreach (var pair in PointerSignatures) result.PointerSignatures[pair.Key] = new List<MethodSig>(pair.Value);
			result.TypeNames.UnionWith(TypeNames);
			result.Strings.UnionWith(Strings);
			result.Methods.UnionWith(Methods);
			result.VirtualMethods.UnionWith(VirtualMethods);
			result.Lookups.UnionWith(Lookups);
			result.DynamicNames.UnionWith(DynamicNames);
			return result;
		}

		public static SymbolicValue Merge(IEnumerable<SymbolicValue> values) {
			var items = values.ToArray();
			if (items.Length == 0 || items.All(value => value.IsUnknown))
				return items.FirstOrDefault(value => value.UnknownReason is not null) ?? Unknown;
			if (items.Any(value => value.IsUnknown)) {
				var partial = Merge(items.Where(value => !value.IsUnknown));
				partial.IsAmbiguous = true; partial.MayHaveUnknownTargets = true; partial.IsExactType = false;
				partial.MayBeNull = true;
				partial.UnknownReason ??= items.FirstOrDefault(value => value.IsUnknown && value.UnknownReason is not null)?.UnknownReason;
				partial.Members = partial.Members.ToImmutableDictionary(pair => pair.Key, pair => Merge(new[] { pair.Value, Unknown }));
				if (partial.Storage is object storage) partial.StorageCandidates.Add(storage);
				partial.Integer = null; partial.Floating = null; partial.Storage = null; return partial;
			}
			var result = new SymbolicValue();
			foreach (string name in items.SelectMany(item => item.Members.Keys).Distinct(StringComparer.Ordinal).Take(64)) {
				var member = Merge(items.Select(item => item.Members.TryGetValue(name, out var value) ? value : Unknown));
				result.Members = result.Members.SetItem(name, member);
			}
			result.AggregateDepth = result.Members.Count == 0 ? 0 : 1 + result.Members.Values.Max(item => item.AggregateDepth);
			result.IsAmbiguous = items.Any(value => value.IsAmbiguous) || items.Any(value => !value.SameAs(items[0]));
			result.MayHaveUnknownTargets = items.Any(value => value.MayHaveUnknownTargets);
			var method_values = items.Where(value => value.Methods.Count != 0).ToArray();
			result.MethodKind = method_values.All(value => value.MethodKind == (method_values.FirstOrDefault()?.MethodKind ?? MethodValueKind.Pointer)) ? method_values.FirstOrDefault()?.MethodKind ?? MethodValueKind.Pointer : MethodValueKind.Mixed;
			result.UnknownReason = items.Select(value => value.UnknownReason).FirstOrDefault(reason => reason is not null);
			if (items.All(value => Equals(value.OpaqueIdentity, items[0].OpaqueIdentity))) result.OpaqueIdentity = items[0].OpaqueIdentity;
			result.IsBoxed = items.All(value => value.IsBoxed);
			result.RepresentsType = items.All(value => value.RepresentsType);
			result.IsExactType = items.All(value => value.IsExactType) && items.All(value => value.TypeNames.SetEquals(items[0].TypeNames));
			result.MayBeNull = items.Any(value => value.MayBeNull || value.Integer?.IsZero == true && !value.IsBoxed);
			var delegate_values = items.Where(value => value.Integer?.IsZero != true || value.IsBoxed).ToArray();
			var invocation_list = delegate_values.FirstOrDefault()?.DelegateList;
			if (invocation_list is not null && delegate_values.All(value => value.DelegateList is not null && value.DelegateList.Count == invocation_list.Count && value.DelegateList.Zip(invocation_list, SameBinding).All(equal => equal))) result.DelegateList = invocation_list;
			if (items.All(value => ReferenceEquals(value.Receiver, items[0].Receiver))) result.Receiver = items[0].Receiver;
			if (items.All(value => ReferenceEquals(value.Integer, items[0].Integer))) result.Integer = items[0].Integer;
			if (items.All(value => SameFloat(value.Floating, items[0].Floating))) result.Floating = items[0].Floating;
			if (items.Any(value => value.IsBoxed) && !result.IsBoxed) result.Integer = null;
			if (items.All(value => Equals(value.Storage, items[0].Storage))) result.Storage = items[0].Storage;
			result.StorageCandidates.UnionWith(items.SelectMany(value => value.PossibleStorage).Take(65));
			if (result.StorageCandidates.Count > 64) { Limit(result.StorageCandidates, 64); result.MayHaveUnknownTargets = true; result.IsAmbiguous = true; result.UnknownReason = "symbolic storage set limit reached"; }
			if (result.Storage is not null) result.StorageCandidates.Remove(result.Storage);
			foreach (var value in items) {
				result.TypeNames.UnionWith(value.TypeNames);
				result.Strings.UnionWith(value.Strings);
				result.Methods.UnionWith(value.Methods);
				result.VirtualMethods.UnionWith(value.VirtualMethods);
				result.Lookups.UnionWith(value.Lookups);
				result.DynamicNames.UnionWith(value.DynamicNames);
				foreach (var pair in value.PointerSignatures) {
					if (!result.PointerSignatures.TryGetValue(pair.Key, out var signatures)) result.PointerSignatures[pair.Key] = signatures = new List<MethodSig>();
					foreach (var signature in pair.Value) if (!signatures.Any(existing => new SigComparer().Equals(existing, signature))) signatures.Add(signature);
				}
			}
			if (result.TypeNames.Count > 64 || result.Strings.Count > 64 || result.Methods.Count > 512 || result.Lookups.Count > 64) {
				Limit(result.TypeNames, 64); Limit(result.Strings, 64); Limit(result.Methods, 512); Limit(result.Lookups, 64);
				result.VirtualMethods.IntersectWith(result.Methods);
				foreach (var method in result.PointerSignatures.Keys.Where(method => !result.Methods.Contains(method)).ToArray()) result.PointerSignatures.Remove(method);
				result.IsAmbiguous = true; result.IsExactType = false; result.MayHaveUnknownTargets = true; result.DelegateList = null;
				result.UnknownReason = "symbolic target/value set limit reached";
			}
			return result;

			static void Limit<T>(HashSet<T> values, int maximum) { if (values.Count <= maximum) return; var retained = values.Take(maximum).ToArray(); values.Clear(); values.UnionWith(retained); }
		}

		static bool SameFloat(double? left, double? right) => left.HasValue == right.HasValue && (!left.HasValue || BitConverter.DoubleToInt64Bits(left.Value) == BitConverter.DoubleToInt64Bits(right!.Value));
		public bool SameAs(SymbolicValue other) => IsUnknown == other.IsUnknown &&
			SameFloat(Floating, other.Floating) &&
			ReferenceEquals(Integer, other.Integer) && Equals(Storage, other.Storage) && StorageCandidates.SetEquals(other.StorageCandidates) &&
			IsAmbiguous == other.IsAmbiguous &&
			IsBoxed == other.IsBoxed &&
			IsExactType == other.IsExactType && ReferenceEquals(Receiver, other.Receiver) &&
			RepresentsType == other.RepresentsType &&
			MayBeNull == other.MayBeNull &&
			MethodKind == other.MethodKind &&
			Members.Count == other.Members.Count && Members.All(pair => other.Members.TryGetValue(pair.Key, out var member) && pair.Value.SameAs(member)) &&
			(ReferenceEquals(DelegateList, other.DelegateList) || DelegateList is not null && other.DelegateList is not null && DelegateList.Count == other.DelegateList.Count && DelegateList.Zip(other.DelegateList, SameBinding).All(equal => equal)) &&
			MayHaveUnknownTargets == other.MayHaveUnknownTargets &&
			Equals(OpaqueIdentity, other.OpaqueIdentity) && PointerSignatures.Count == other.PointerSignatures.Count && PointerSignatures.All(pair => other.PointerSignatures.TryGetValue(pair.Key, out var values) && pair.Value.Count == values.Count && pair.Value.All(signature => values.Any(value => new SigComparer().Equals(value, signature)))) &&
			TypeNames.SetEquals(other.TypeNames) && Strings.SetEquals(other.Strings) &&
			Methods.SetEquals(other.Methods) && VirtualMethods.SetEquals(other.VirtualMethods) &&
			Lookups.SetEquals(other.Lookups) && DynamicNames.SetEquals(other.DynamicNames);
	}

	sealed class SymbolicState {
		const int MaximumMergedStackDepth = 64;
		public List<SymbolicValue> Stack { get; }
		public Dictionary<int, SymbolicValue> Locals { get; }
		public Dictionary<int, SymbolicValue> Arguments { get; } = new Dictionary<int, SymbolicValue>();
		public ImmutableDictionary<object, SymbolicValue> Memory { get; private set; } = ImmutableDictionary<object, SymbolicValue>.Empty;
		public object Frame { get; set; } = new object();
		public object MemoryStamp { get; private set; } = new object();
		public bool HasWrites { get; private set; }
		public ImmutableHashSet<FinallyContinuation> FinallyTargets { get; set; } = ImmutableHashSet<FinallyContinuation>.Empty;
		public bool ExceptionalPath { get; set; }
		public bool PendingException { get; set; }
		public bool PathTerminated { get; set; }
		public bool ConservativeContext { get; set; }
		public ImmutableHashSet<object> EscapedStorage { get; set; } = ImmutableHashSet<object>.Empty;
		ImmutableHashSet<object> byte_addressed_storage = ImmutableHashSet<object>.Empty;
		public void Store(object address, SymbolicValue value) {
			if (address is FlowAddress { Kind: "aggregate member" } member) {
				var aggregate = Load(member.Owner);
				Store(member.Owner, aggregate.WithMember((string)member.Slot, value)); return;
			}
			if (Memory.TryGetValue(address, out var previous) && previous.SameAs(value)) return;
			var root = FlowMemory.AddressRoot(address)!;
			if (address is FlowAddress { Kind: "byte offset" }) byte_addressed_storage = byte_addressed_storage.Add(root);
			if (byte_addressed_storage.Contains(root)) foreach (var alias in Memory.Keys.Where(key => !Equals(key, address) && Equals(FlowMemory.AddressRoot(key), root)).ToArray()) Memory = Memory.SetItem(alias, SymbolicValue.UnknownBecause("overlapping byte-addressed storage was written"));
			Memory = Memory.SetItem(address, value);
			MemoryStamp = new object(); HasWrites = true;
		}
		public SymbolicValue Load(object address) {
			if (address is FlowAddress { Kind: "aggregate member" } member) {
				var aggregate = Load(member.Owner);
				return aggregate.Members.TryGetValue((string)member.Slot, out var item) ? item : SymbolicValue.Unknown;
			}
			if (Memory.TryGetValue(address, out var value)) return value;
			if (address is FlowAddress slot && Equals(slot.Owner, Frame) && slot.Slot is int index) {
				if (slot.Kind == "local" && Locals.TryGetValue(index, out value)) return value;
				if (slot.Kind == "argument" && Arguments.TryGetValue(index, out value)) return value;
			}
			return SymbolicValue.Unknown;
		}
		public void RefineNonNull(SymbolicValue tested) {
			if (tested.IsUnknown) return;
			var value = tested.NonNull();
			for (int index = 0; index < Stack.Count; index++) if (ReferenceEquals(Stack[index], tested)) Stack[index] = value;
			foreach (var key in Locals.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) Locals[key] = value;
			foreach (var key in Arguments.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) Arguments[key] = value;
			foreach (var key in Memory.Where(pair => ReferenceEquals(pair.Value, tested)).Select(pair => pair.Key).ToArray()) Store(key, value);
		}
		public void ImportMemory(SymbolicState other) {
			EscapedStorage = other.EscapedStorage;
			byte_addressed_storage = other.byte_addressed_storage;
			Memory = other.Memory;
			MemoryStamp = other.MemoryStamp; HasWrites |= other.HasWrites;
			ConservativeContext |= other.ConservativeContext;
		}

		public SymbolicState() {
			Stack = new List<SymbolicValue>();
			Locals = new Dictionary<int, SymbolicValue>();
		}

		SymbolicState(List<SymbolicValue> stack, Dictionary<int, SymbolicValue> locals) {
			Stack = stack;
			Locals = locals;
		}

		public SymbolicState Clone() {
			var result = new SymbolicState(new List<SymbolicValue>(Stack), new Dictionary<int, SymbolicValue>(Locals)) { Frame = Frame, MemoryStamp = MemoryStamp, HasWrites = HasWrites, FinallyTargets = FinallyTargets, ExceptionalPath = ExceptionalPath, PendingException = PendingException, PathTerminated = PathTerminated, ConservativeContext = ConservativeContext, EscapedStorage = EscapedStorage };
			foreach (var pair in Arguments) result.Arguments[pair.Key] = pair.Value;
			result.Memory = Memory;
			result.byte_addressed_storage = byte_addressed_storage;
			return result;
		}

		public bool MergeFrom(SymbolicState other) {
			bool changed = false;
			if (!other.byte_addressed_storage.IsSubsetOf(byte_addressed_storage)) { byte_addressed_storage = byte_addressed_storage.Union(other.byte_addressed_storage); changed = true; }
			if (!other.EscapedStorage.IsSubsetOf(EscapedStorage)) { EscapedStorage = EscapedStorage.Union(other.EscapedStorage); changed = true; }
			if (other.ConservativeContext && !ConservativeContext) { ConservativeContext = true; changed = true; }
			if (other.ExceptionalPath && !ExceptionalPath) { ExceptionalPath = true; changed = true; }
			if (!other.FinallyTargets.IsSubsetOf(FinallyTargets)) {
				FinallyTargets = FinallyTargets.Union(other.FinallyTargets); changed = true;
				if (FinallyTargets.Count > 64) throw new InvalidOperationException("finally continuation limit reached");
			}
			foreach (var key in ReferenceEquals(Memory, other.Memory) ? Enumerable.Empty<object>() : Memory.Keys.Union(other.Memory.Keys).ToArray()) {
				var merged = SymbolicValue.Merge(new[] { Load(key), other.Load(key) });
				if (!Memory.TryGetValue(key, out var previous) || !previous.SameAs(merged)) { Store(key, merged); changed = true; }
			}
			foreach (int key in Arguments.Keys.Union(other.Arguments.Keys).ToArray()) {
				var left = Arguments.TryGetValue(key, out var a) ? a : SymbolicValue.Unknown;
				var right = other.Arguments.TryGetValue(key, out var b) ? b : SymbolicValue.Unknown;
				var merged = SymbolicValue.Merge(new[] { left, right });
				if (!left.SameAs(merged) || !Arguments.ContainsKey(key)) { Arguments[key] = merged; changed = true; }
			}
			if (Stack.Count != other.Stack.Count) {
				int count = Math.Min(MaximumMergedStackDepth, Math.Max(Stack.Count, other.Stack.Count));
				var merged_stack = Enumerable.Repeat(SymbolicValue.Unknown, count).ToArray();
				if (Stack.Count != count || Stack.Where((value, index) => !value.SameAs(merged_stack[index])).Any()) {
					Stack.Clear();
					Stack.AddRange(merged_stack);
					changed = true;
				}
			}
			else {
				for (int i = 0; i < Stack.Count; i++) {
					var merged = SymbolicValue.Merge(new[] { Stack[i], other.Stack[i] });
					if (Stack[i].SameAs(merged))
						continue;
					Stack[i] = merged;
					changed = true;
				}
			}
			foreach (int index in Locals.Keys.Union(other.Locals.Keys).ToArray()) {
				var left = Locals.TryGetValue(index, out var left_value) ? left_value : SymbolicValue.Unknown;
				var right = other.Locals.TryGetValue(index, out var right_value) ? right_value : SymbolicValue.Unknown;
				var merged = SymbolicValue.Merge(new[] { left, right });
				if (left.SameAs(merged) && Locals.ContainsKey(index))
					continue;
				Locals[index] = merged;
				changed = true;
			}
			return changed;
		}
	}

	sealed class PointerFlowCatalog {
		readonly Dictionary<FieldDef, SymbolicValue> field_values = new Dictionary<FieldDef, SymbolicValue>();
		readonly Dictionary<IField, FieldDef?> resolved_fields = new Dictionary<IField, FieldDef?>();
		readonly Dictionary<(object Storage, long Index), SymbolicValue> elements = new Dictionary<(object, long), SymbolicValue>();
		readonly Dictionary<object, Dictionary<long, SymbolicValue>> array_elements = new Dictionary<object, Dictionary<long, SymbolicValue>>();

		public bool MergeField(FieldDef field, SymbolicValue value) => MergeInto(field_values, field, value);
		public void SetField(FieldDef field, SymbolicValue value) => field_values[field] = value;
		public void SetSlot(object address, SymbolicValue value) {
			elements[(address, 0)] = value;
			if (address is FlowAddress { Kind: "element", Slot: long index } slot) {
				if (!array_elements.TryGetValue(slot.Owner, out var entries)) array_elements[slot.Owner] = entries = new Dictionary<long, SymbolicValue>();
				entries[index] = value;
			}
		}

		public SymbolicValue FieldValue(FieldDef? field) => field is not null && field_values.TryGetValue(field, out var value)
			? value.Clone() : SymbolicValue.Unknown;

		public bool MergeElement(object storage, long index, SymbolicValue value) => MergeInto(elements, (storage, index), value);
		public SymbolicValue ElementValue(object? storage, IntegerExpression? index) => storage is not null && index?.IsConstant == true &&
			elements.TryGetValue((storage, index.Value), out var value) ? value.Clone() : SymbolicValue.Unknown;
		public SymbolicValue SlotValue(object? storage) => storage is not null && elements.TryGetValue((storage, 0), out var value)
			? value.Clone() : SymbolicValue.Unknown;
		public IEnumerable<(long Index, SymbolicValue Value)> ArrayElements(object storage) => array_elements.TryGetValue(storage, out var entries) ? entries.Select(pair => (pair.Key, pair.Value)) : Enumerable.Empty<(long, SymbolicValue)>();

		public FieldDef? ResolveField(IField? reference) {
			if (reference is null)
				return null;
			if (reference is FieldDef definition)
				return definition;
			if (resolved_fields.TryGetValue(reference, out var cached))
				return cached;
			FieldDef? result;
			try { result = reference.ResolveFieldDef(); }
			catch (Exception) { result = null; }
			resolved_fields[reference] = result;
			return result;
		}

		static bool MergeInto<TKey>(Dictionary<TKey, SymbolicValue> values, TKey key, SymbolicValue value)
			where TKey : notnull {
			if (value.IsUnknown || value.Methods.Count == 0 && value.Storage is null && value.StorageCandidates.Count == 0 && value.Integer is null && value.Floating is null && !value.IsBoxed && value.Strings.Count == 0 && value.TypeNames.Count == 0 && value.Lookups.Count == 0) {
				if (values.TryGetValue(key, out var previous) && previous.IsUnknown) return false;
				values[key] = SymbolicValue.Unknown; return true;
			}
			var merged = values.TryGetValue(key, out var current)
				? SymbolicValue.Merge(new[] { current, value }) : value.Clone();
			if (values.TryGetValue(key, out current) && current.SameAs(merged))
				return false;
			values[key] = merged;
			return true;
		}
	}

	static class WorkspaceCallResolver {
		public static void AddExtendedEdges(HashSet<MethodDef> methods, IEnumerable<VirtualCallSite> virtual_sites,
			IEnumerable<ReflectionTarget> reflection_sites,
			Dictionary<(MethodDef Source, MethodDef Target), CallSiteAccumulator> call_sites) {
			var type_map = methods.Select(method => method.DeclaringType).Where(type => type is not null).Distinct()
				.GroupBy(CallGraphIndex.SafeTypeName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var signature_map = methods.GroupBy(MethodSignatureKey, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var explicit_overrides = BuildExplicitOverrideMap(methods);
			var method_name_map = methods.GroupBy(CallGraphIndex.SafeMethodName, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
			var relation_cache = new Dictionary<(TypeDef Candidate, TypeDef Declaration, bool Interface), bool>();
			foreach (var site in virtual_sites.GroupBy(site => (site.Source, site.Declaration, site.Offset, site.Operation))
				.Select(group => group.First())) {
				foreach (var target in FindVirtualTargets(site.Declaration, signature_map, explicit_overrides,
					type_map, relation_cache))
					CallGraphIndex.AddCallSite(call_sites, site.Source, target, site.Offset, site.Operation, true);
			}
			foreach (var site in reflection_sites.GroupBy(site => (site.Source, site.Offset, site.Operation,
					Types: string.Join("\0", site.TypeNames), Names: string.Join("\0", site.MethodNames)))
				.Select(group => group.First())) {
				foreach (var target in ResolveReflectionTargets(site, methods, type_map, method_name_map))
					CallGraphIndex.AddCallSite(call_sites, site.Source, target, site.Offset, site.Operation, true);
			}
		}

		static Dictionary<string, MethodDef[]> BuildExplicitOverrideMap(IEnumerable<MethodDef> methods) {
			var result = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
			foreach (var method in methods) {
				foreach (var method_override in method.Overrides) {
					string key = MethodIdentity(method_override.MethodDeclaration);
					if (!result.TryGetValue(key, out var values))
						result.Add(key, values = new List<MethodDef>());
					values.Add(method);
				}
			}
			return result.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToArray(), StringComparer.Ordinal);
		}

		static IEnumerable<MethodDef> FindVirtualTargets(MethodDef declaration,
			Dictionary<string, MethodDef[]> signature_map, Dictionary<string, MethodDef[]> explicit_overrides,
			Dictionary<string, TypeDef[]> type_map,
			Dictionary<(TypeDef Candidate, TypeDef Declaration, bool Interface), bool> relation_cache) {
			var result = new HashSet<MethodDef>();
			if (explicit_overrides.TryGetValue(MethodIdentity(declaration), out var explicit_methods))
				result.UnionWith(explicit_methods.Where(IsRuntimeMethod));
			if (declaration.DeclaringType is not TypeDef declaring_type)
				return result;
			bool is_interface = declaring_type.IsInterface;
			if (signature_map.TryGetValue(MethodSignatureKey(declaration), out var candidates)) {
				foreach (var candidate in candidates) {
					if (ReferenceEquals(candidate, declaration) || !IsRuntimeMethod(candidate) ||
						candidate.DeclaringType is not TypeDef candidate_type)
						continue;
					if (!is_interface && (!candidate.IsVirtual || candidate.IsNewSlot))
						continue;
					if (candidate.MethodSig.Params.Count != declaration.MethodSig.Params.Count || !candidate.MethodSig.Params.Zip(declaration.MethodSig.Params, (a, b) => new SigComparer().Equals(a, b)).All(equal => equal)) continue;
					var key = (candidate_type, declaring_type, is_interface);
					if (!relation_cache.TryGetValue(key, out bool related)) {
						related = is_interface ? ImplementsInterface(candidate_type, declaring_type, type_map) :
							DerivesFrom(candidate_type, declaring_type, type_map);
						relation_cache[key] = related;
					}
					if (related)
						result.Add(candidate);
				}
			}
			return result;
		}

		static IEnumerable<MethodDef> ResolveReflectionTargets(ReflectionTarget site, HashSet<MethodDef> methods,
			Dictionary<string, TypeDef[]> type_map, Dictionary<string, MethodDef[]> method_name_map) {
			var result = new HashSet<MethodDef>(site.ExactMethods.Where(methods.Contains));
			if (site.TypeNames.Count == 0 || site.MethodNames.Count == 0)
				return result;
			foreach (string type_name in site.TypeNames) {
				if (!type_map.TryGetValue(type_name, out var requested_types))
					continue;
				foreach (string method_name in site.MethodNames) {
					if (!method_name_map.TryGetValue(method_name, out var candidates))
						continue;
					var matches = candidates.Where(candidate => candidate.DeclaringType is TypeDef candidate_type &&
						requested_types.Any(requested => ReferenceEquals(candidate_type, requested) ||
							DerivesFrom(requested, candidate_type, type_map))).Take(2).ToArray();
					if (matches.Length == 1) result.Add(matches[0]);
				}
			}
			return result;
		}

		static bool ImplementsInterface(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>();
			queue.Enqueue(candidate);
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				foreach (var implementation in current.Interfaces) {
					foreach (var resolved in ResolveTypes(implementation.Interface, type_map)) {
						if (SameType(resolved, target))
							return true;
						queue.Enqueue(resolved);
					}
				}
				foreach (var base_type in ResolveTypes(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static bool DerivesFrom(TypeDef candidate, TypeDef target, Dictionary<string, TypeDef[]> type_map) {
			var visited = new HashSet<TypeDef>();
			var queue = new Queue<TypeDef>(ResolveTypes(candidate.BaseType, type_map));
			while (queue.Count != 0) {
				var current = queue.Dequeue();
				if (!visited.Add(current))
					continue;
				if (SameType(current, target))
					return true;
				foreach (var base_type in ResolveTypes(current.BaseType, type_map))
					queue.Enqueue(base_type);
			}
			return false;
		}

		static IEnumerable<TypeDef> ResolveTypes(ITypeDefOrRef? reference, Dictionary<string, TypeDef[]> type_map) {
			if (reference is null)
				yield break;
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
			if (type_map.TryGetValue(CallGraphIndex.SafeTypeName(reference), out var candidates))
				foreach (var candidate in candidates)
					if (reference.DefinitionAssembly is null || StringComparer.OrdinalIgnoreCase.Equals(reference.DefinitionAssembly.FullName, candidate.DefinitionAssembly?.FullName)) yield return candidate;
		}

		static bool SameType(TypeDef left, TypeDef right) => ReferenceEquals(left, right) ||
			StringComparer.Ordinal.Equals(CallGraphIndex.SafeTypeName(left), CallGraphIndex.SafeTypeName(right)) &&
			StringComparer.Ordinal.Equals(left.DefinitionAssembly?.FullName, right.DefinitionAssembly?.FullName);

		static bool IsRuntimeMethod(MethodDef method) {
			try { return !method.IsStatic && !method.IsAbstract && method.HasBody; }
			catch (Exception) { return false; }
		}

		static string MethodSignatureKey(MethodDef method) {
			try {
				return CallGraphIndex.SafeMethodName(method) + "`" + method.GenericParameters.Count + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return CallGraphIndex.SafeMethodName(method); }
		}

		static string MethodIdentity(IMethod method) {
			try {
				return method.DeclaringType?.DefinitionAssembly?.FullName + "|" + CallGraphIndex.SafeTypeName(method.DeclaringType) + "::" +
					(method.Name?.ToString() ?? string.Empty) + "(" +
					string.Join(",", method.MethodSig?.Params.Select(type => type.FullName) ?? Enumerable.Empty<string>()) + ")";
			}
			catch (Exception) { return method.FullName ?? string.Empty; }
		}
	}
}
