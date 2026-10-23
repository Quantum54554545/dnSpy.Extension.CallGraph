using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record FlowAddress(string Kind, object Owner, object Slot);

	sealed class FlowMemory {
		readonly PointerFlowCatalog catalog;
		readonly Func<FieldDef?, SymbolicValue> field_value;
		readonly IntegerExpression.Factory integers;
		sealed record ArrayRead((long Index, SymbolicValue Value, bool Catalog)[] Entries, SymbolicValue Length, bool Conservative, SymbolicValue Result);
		readonly Dictionary<object, ArrayRead> array_reads = new Dictionary<object, ArrayRead>();
		public FlowMemory(PointerFlowCatalog catalog, Func<FieldDef?, SymbolicValue> field_value, IntegerExpression.Factory integers) {
			this.catalog = catalog; this.field_value = field_value; this.integers = integers;
		}
		public static FlowAddress Local(SymbolicState state, int index) => new FlowAddress("local", state.Frame, index);
		public static FlowAddress Argument(SymbolicState state, int index) => new FlowAddress("argument", state.Frame, index);
		public static SymbolicValue ReadObject(SymbolicState state, SymbolicValue value) {
			if (value.Storage is FlowAddress address && (state.Memory.ContainsKey(address) || address.Kind == "aggregate member")) return state.Load(address);
			if (value.TypeNames.Count == 0 && value.Storage is null && value.StorageCandidates.Count != 0 && value.StorageCandidates.All(address => address is FlowAddress && state.Memory.ContainsKey(address))) return SymbolicValue.Merge(value.StorageCandidates.Select(state.Load).Concat(value.MayHaveUnknownTargets ? new[] { SymbolicValue.Unknown } : Array.Empty<SymbolicValue>()));
			return value;
		}
		public static bool IsAddress(SymbolicValue value) => value.Storage is not null && value.TypeNames.Count == 0 && !value.IsBoxed && !value.IsAmbiguous;
		public static object? AddressRoot(object? address) { while (address is FlowAddress { Kind: "byte offset" } offset) address = offset.Owner; return address; }
		public static SymbolicValue Offset(SymbolicValue pointer, long amount, bool subtract) {
			try {
				long offset = subtract ? checked(-amount) : amount; object owner = pointer.Storage!;
				if (owner is FlowAddress { Kind: "byte offset", Slot: long previous } address) { offset = checked(offset + previous); owner = address.Owner; }
				return SymbolicValue.FromStorage(offset == 0 ? owner : new FlowAddress("byte offset", owner, offset));
			}
			catch (OverflowException) { return SymbolicValue.UnknownBecause("address offset overflow"); }
		}
		public bool Execute(MethodDef method, Instruction instruction, SymbolicState state, IReadOnlyList<SymbolicValue> arguments) {
			var code = instruction.OpCode.Code;
			if (code == Code.Localloc) {
				var size = Pop(state); var allocation = new FlowAddress("stack allocation", state.Frame, instruction);
				if (state.Memory.ContainsKey(new FlowAddress("length", allocation, 0))) { state.Stack.Add(SymbolicValue.UnknownBecause("repeated stack allocation site has no exact address summary")); return true; }
				state.Store(new FlowAddress("length", allocation, 0), size);
				state.Stack.Add(SymbolicValue.FromStorage(allocation)); return true;
			}
			if (code is Code.Ldloca or Code.Ldloca_S && instruction.Operand is Local local) {
				var address = Local(state, local.Index);
				if (!state.Memory.ContainsKey(address)) state.Store(address, state.Locals.TryGetValue(local.Index, out var value) ? value : InitialLocal(method, local, address));
				state.Stack.Add(SymbolicValue.FromStorage(address)); return true;
			}
			if (code is Code.Ldarga or Code.Ldarga_S && instruction.Operand is Parameter parameter) {
				var address = Argument(state, parameter.Index);
				if (!state.Memory.ContainsKey(address)) state.Store(address, state.Arguments.TryGetValue(parameter.Index, out var value) ? value : parameter.Index < arguments.Count ? arguments[parameter.Index] : SymbolicValue.Unknown);
				state.Stack.Add(SymbolicValue.FromStorage(address)); return true;
			}
			if (code is Code.Starg or Code.Starg_S && instruction.Operand is Parameter destination) {
				var value = Pop(state); state.Arguments[destination.Index] = value; state.Store(Argument(state, destination.Index), value); return true;
			}
			if (code is Code.Ldsfld or Code.Ldsflda or Code.Stsfld) {
				var field = catalog.ResolveField(instruction.Operand as IField);
				if (field is null) { if (code == Code.Stsfld) Pop(state); else state.Stack.Add(SymbolicValue.Unknown); return true; }
				var initial = field_value(field);
				if (state.ConservativeContext && !field.IsLiteral) initial = Uncertain(initial, "field value may have changed after incomplete value-flow analysis");
				if (code == Code.Stsfld) state.Store(field, Pop(state));
				else if (code == Code.Ldsflda) { if (!field.HasFieldRVA && !state.Memory.ContainsKey(field)) state.Store(field, initial.IsUnknown ? SymbolicValue.Opaque(new FlowAddress("unknown field value", field, state.MemoryStamp)) : initial); state.Stack.Add(SymbolicValue.FromStorage(field)); }
				else {
					var value = state.Memory.TryGetValue(field, out var stored) ? stored : initial;
					if (value.IsUnknown) { value = SymbolicValue.Opaque(new FlowAddress("unknown field value", field, state.MemoryStamp)); state.Store(field, value); }
					else if (!state.Memory.ContainsKey(field)) state.Store(field, value);
					state.Stack.Add(value);
				}
				return true;
			}
			if (code is Code.Ldfld or Code.Ldflda or Code.Stfld) {
				var value = code == Code.Stfld ? Pop(state) : SymbolicValue.Unknown;
				var raw_instance = Pop(state);
				var instance = ReadObject(state, raw_instance);
				if (ContextCallEvaluator.IsDefinitelyNull(instance)) { state.PathTerminated = true; return true; }
				if (instruction.Operand is IField aggregate_field && instance.Members.TryGetValue(aggregate_field.Name, out var member)) {
					if (code == Code.Ldfld) state.Stack.Add(member);
					else if (code == Code.Stfld && raw_instance.Storage is object location) state.Store(location, instance.WithMember(aggregate_field.Name, value));
					else if (code == Code.Ldflda && raw_instance.Storage is object location_address) {
						var member_address = new FlowAddress("aggregate member", location_address, aggregate_field.Name.ToString());
						state.Store(member_address, member); state.Stack.Add(SymbolicValue.FromStorage(member_address));
					}
					else if (code == Code.Ldflda) state.Stack.Add(SymbolicValue.Unknown);
					return true;
				}
				var field = catalog.ResolveField(instruction.Operand as IField);
				if (code == Code.Ldfld && instruction.Operand is IField reference && reference.Name == "Target" && instance.DynamicNames.Count != 0 && reference.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.CallSite`", StringComparison.Ordinal)) { state.Stack.Add(instance); return true; }
				object? address = instance.Storage is not null && field is not null ? new FlowAddress("field", instance.Storage, field) : null;
				if (code == Code.Stfld) {
					if (!instance.IsExactType && field is not null) foreach (var key in new List<object>(state.Memory.Keys)) if (key is FlowAddress slot && slot.Kind == "field" && Equals(slot.Slot, field) && !Equals(slot, address)) state.Store(key, SymbolicValue.Unknown);
					if (field is not null) foreach (var owner in instance.PossibleStorage) { var location = new FlowAddress("field", owner, field); state.Store(location, address is not null && !instance.IsAmbiguous && !instance.MayHaveUnknownTargets ? value : SymbolicValue.Merge(new[] { Read(state, location), value })); }
				}
				else state.Stack.Add(field is null ? SymbolicValue.Unknown : ReadCandidates(instance, owner => code == Code.Ldflda ? SymbolicValue.FromStorage(new FlowAddress("field", owner, field)) : Read(state, new FlowAddress("field", owner, field))));
				return true;
			}
			if (code == Code.Newarr) {
				var length = Pop(state);
				object identity = new FlowAddress("allocation", state.Frame, instruction);
				bool repeated = state.Memory.ContainsKey(new FlowAddress("length", identity, 0));
				if (repeated) identity = new FlowAddress("summary allocation", state.Frame, instruction);
				var length_slot = new FlowAddress("length", identity, 0);
				state.Store(length_slot, repeated && state.Memory.ContainsKey(length_slot) ? SymbolicValue.Merge(new[] { state.Load(length_slot), length }) : length);
				string array_type = (instruction.Operand as ITypeDefOrRef)?.FullName + "[]";
				state.Stack.Add(repeated ? SymbolicValue.FromReference(array_type, identity, false) : SymbolicValue.FromObject(array_type, identity)); return true;
			}
			if (code == Code.Ldlen) {
				var array = Pop(state); if (ContextCallEvaluator.IsDefinitelyNull(array)) state.PathTerminated = true;
				state.Stack.Add(ReadCandidates(array, owner => Read(state, new FlowAddress("length", owner, 0)))); return true;
			}
			if (code is Code.Stelem or Code.Stelem_I or Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_Ref) {
				var value = Pop(state); var index = Pop(state).Integer; var array_value = Pop(state);
				if (ArrayAccessFails(state, array_value, index)) { state.PathTerminated = true; return true; }
				value = Narrow(code, value);
				foreach (var array in array_value.PossibleStorage) {
					if (InvalidIndex(state, array, index)) continue;
					if (index?.IsConstant == true) { var address = new FlowAddress("element", array, index.Value); state.Store(address, array_value.Storage is not null && !array_value.IsAmbiguous && !array_value.MayHaveUnknownTargets ? value : SymbolicValue.Merge(new[] { Read(state, address), value })); }
					else {
						var addresses = catalog.ArrayElements(array).Select(pair => (object)new FlowAddress("element", array, pair.Index)).Concat(state.Memory.Keys.Where(key => key is FlowAddress slot && slot.Kind == "element" && Equals(slot.Owner, array))).Distinct().Take(512).ToArray();
						foreach (var address in addresses) state.Store(address, SymbolicValue.Merge(new[] { Read(state, address), value }));
					}
				}
				return true;
			}
			if (code is Code.Ldelem or Code.Ldelem_I or Code.Ldelem_I1 or Code.Ldelem_U1 or Code.Ldelem_I2 or Code.Ldelem_U2 or Code.Ldelem_I4 or Code.Ldelem_U4 or Code.Ldelem_I8 or Code.Ldelem_Ref or Code.Ldelema) {
				var index = Pop(state).Integer; var array_value = Pop(state);
				if (ArrayAccessFails(state, array_value, index)) { state.PathTerminated = true; state.Stack.Add(SymbolicValue.Unknown); return true; }
				var element = ReadCandidates(array_value, array => ReadElement(state, array, index, code == Code.Ldelema));
				state.Stack.Add(code == Code.Ldelema ? element : Narrow(code, element)); return true;
			}
			if (code is Code.Ldind_I or Code.Ldind_I1 or Code.Ldind_U1 or Code.Ldind_I2 or Code.Ldind_U2 or Code.Ldind_I4 or Code.Ldind_U4 or Code.Ldind_I8 or Code.Ldind_Ref or Code.Ldobj) {
				var address = Pop(state); state.Stack.Add(ReadCandidates(address, candidate => ReadRva(method, state, candidate, code) ?? Narrow(code, Read(state, candidate)))); return true;
			}
			if (code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_Ref or Code.Stobj) {
				var value = Narrow(code, Pop(state)); var address = Pop(state);
				foreach (var candidate in address.PossibleStorage) state.Store(candidate, address.Storage is not null && !address.IsAmbiguous ? value : SymbolicValue.Merge(new[] { Read(state, candidate), value })); return true;
			}
			if (code == Code.Cpobj) {
				var source = Pop(state); var copy_destination = Pop(state); var value = ReadCandidates(source, address => Read(state, address));
				foreach (var address in copy_destination.PossibleStorage) state.Store(address, copy_destination.Storage is not null && !copy_destination.IsAmbiguous ? value : SymbolicValue.Merge(new[] { Read(state, address), value })); return true;
			}
			if (code == Code.Initobj) {
				var init_destination = Pop(state);
				foreach (var address in init_destination.PossibleStorage) { var value = Initial(instruction.Operand as ITypeDefOrRef, address); state.Store(address, init_destination.Storage is not null && !init_destination.IsAmbiguous ? value : SymbolicValue.Merge(new[] { Read(state, address), value })); } return true;
			}
			return false;
		}
		static SymbolicValue Uncertain(SymbolicValue value, string reason) => SymbolicValue.Merge(new[] { value, SymbolicValue.UnknownBecause(reason) });
		bool InvalidIndex(SymbolicState state, object array, IntegerExpression? index) {
			if (index?.IsConstant != true) return false;
			if (index.Value < 0) return true;
			var address = new FlowAddress("length", array, 0);
			var length = (state.Memory.ContainsKey(address) ? state.Load(address) : catalog.SlotValue(address)).Integer;
			return length?.IsConstant == true && index.Value >= length.Value;
		}
		bool ArrayAccessFails(SymbolicState state, SymbolicValue array, IntegerExpression? index) => ContextCallEvaluator.IsDefinitelyNull(array) || index?.IsConstant == true && (index.Value < 0 || !array.MayHaveUnknownTargets && array.PossibleStorage.Any() && array.PossibleStorage.All(owner => InvalidIndex(state, owner, index)));
		static SymbolicValue ReadCandidates(SymbolicValue pointer, Func<object, SymbolicValue> read) {
			var values = pointer.PossibleStorage.Select(read);
			return SymbolicValue.Merge(pointer.MayHaveUnknownTargets || pointer.IsUnknown ? values.Append(SymbolicValue.UnknownBecause(pointer.UnknownReason ?? "storage identity is incomplete")) : values);
		}
		SymbolicValue ReadElement(SymbolicState state, object array, IntegerExpression? index, bool address_only) {
			if (InvalidIndex(state, array, index)) return SymbolicValue.UnknownBecause("array index is outside this candidate object's bounds");
			if (index?.IsConstant == true) {
				var address = new FlowAddress("element", array, index.Value);
				if (address_only) return SymbolicValue.FromStorage(address);
				return state.Memory.TryGetValue(address, out var value) ? value : !catalog.SlotValue(address).IsUnknown ? Read(state, address) : catalog.ElementValue(array, index);
			}
			if (address_only) return SymbolicValue.Unknown;
			var elements = catalog.ArrayElements(array).Take(512).ToDictionary(pair => pair.Index, pair => (pair.Value, Catalog: true));
			foreach (var pair in state.Memory.Where(pair => pair.Key is FlowAddress slot && slot.Kind == "element" && Equals(slot.Owner, array))) elements[(long)((FlowAddress)pair.Key).Slot] = (pair.Value, false);
			var entries = elements.OrderBy(pair => pair.Key).Take(512).Select(pair => (Index: pair.Key, pair.Value.Value, pair.Value.Catalog)).ToArray();
			var length_value = Read(state, new FlowAddress("length", array, 0));
			if (array_reads.TryGetValue(array, out var cached) && cached.Conservative == state.ConservativeContext && cached.Length.SameAs(length_value) && entries.Length == cached.Entries.Length && entries.Zip(cached.Entries, (left, right) => left.Index == right.Index && left.Catalog == right.Catalog && ReferenceEquals(left.Value, right.Value)).All(same => same)) return cached.Result;
			var values = entries.Select(pair => pair.Catalog && state.ConservativeContext ? Uncertain(pair.Value, "array contents may have changed") : pair.Value).ToArray();
			var length = length_value.Integer;
			var result = SymbolicValue.Merge(length?.IsConstant == true && length.Value == values.Length ? values : values.Append(SymbolicValue.UnknownBecause("array contents are incomplete")));
			if (array_reads.Count >= 4096) array_reads.Clear();
			array_reads[array] = new ArrayRead(entries, length_value, state.ConservativeContext, result);
			return result;
		}
		SymbolicValue? ReadRva(MethodDef method, SymbolicState state, object address, Code code) {
			if (AddressRoot(address) is not FieldDef field || !field.HasFieldRVA) return null;
			uint size = field.GetFieldSize();
			if (size == 0 || size > 1048576) return SymbolicValue.UnknownBecause("RVA data size is outside the supported read limit");
			if (field.InitialValue is not byte[] data) return SymbolicValue.UnknownBecause("RVA data is unavailable");
			if (method.Module.Machine is not dnlib.PE.Machine.I386 and not dnlib.PE.Machine.AMD64) return SymbolicValue.UnknownBecause("RVA data endianness is not modeled for this architecture");
			if (state.Memory.Keys.Any(key => Equals(AddressRoot(key), field))) return SymbolicValue.UnknownBecause("RVA storage may have been modified");
			long offset = address is FlowAddress { Kind: "byte offset", Slot: long value } ? value : 0;
			int width = code switch { Code.Ldind_I1 or Code.Ldind_U1 => 1, Code.Ldind_I2 or Code.Ldind_U2 => 2, Code.Ldind_I4 or Code.Ldind_U4 => 4, Code.Ldind_I8 => 8, Code.Ldind_I => method.Module.GetPointerSize(0), _ => 0 };
			if (width == 0 || offset < 0 || offset > data.Length - width) return SymbolicValue.UnknownBecause("RVA read width or offset is outside modeled data");
			var bytes = data.AsSpan((int)offset, width);
			long number = width switch { 1 => code == Code.Ldind_I1 ? (sbyte)bytes[0] : bytes[0], 2 => code == Code.Ldind_I2 ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes), 4 => BinaryPrimitives.ReadInt32LittleEndian(bytes), _ => BinaryPrimitives.ReadInt64LittleEndian(bytes) };
			return SymbolicValue.FromInteger(integers.Constant(number, width == 8));
		}
		SymbolicValue Narrow(Code code, SymbolicValue value) {
			var conversion = code switch {
				Code.Stelem_I1 or Code.Stind_I1 or Code.Ldelem_I1 or Code.Ldind_I1 => Code.Conv_I1,
				Code.Ldelem_U1 or Code.Ldind_U1 => Code.Conv_U1,
				Code.Stelem_I2 or Code.Stind_I2 or Code.Ldelem_I2 or Code.Ldind_I2 => Code.Conv_I2,
				Code.Ldelem_U2 or Code.Ldind_U2 => Code.Conv_U2,
				Code.Stelem_I4 or Code.Stind_I4 or Code.Ldelem_I4 or Code.Ldind_I4 or Code.Ldelem_U4 or Code.Ldind_U4 => Code.Conv_I4,
				_ => Code.Nop,
			};
			return conversion == Code.Nop ? value : SymbolicValue.FromInteger(integers.Convert(conversion, value.Integer));
		}
		internal SymbolicValue InitialLocal(MethodDef method, Local local, object address) {
			if (!method.Body.InitLocals) return SymbolicValue.Unknown;
			var signature = local.Type.RemovePinnedAndModifiers();
			if (signature.ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SZArray or ElementType.Array) return SymbolicValue.FromInteger(integers.Constant(0));
			if (signature.ElementType == ElementType.ValueType) return SymbolicValue.FromObject(signature.FullName, address);
			return Initial(signature.ToTypeDefOrRef(), address);
		}
		SymbolicValue Initial(ITypeDefOrRef? type, object address) {
			if (type is null) return SymbolicValue.Unknown;
			var signature = type.ToTypeSig();
			if (signature.ElementType is ElementType.Boolean or ElementType.Char or ElementType.I1 or ElementType.U1 or ElementType.I2 or ElementType.U2 or ElementType.I4 or ElementType.U4 or ElementType.I or ElementType.U or ElementType.I8 or ElementType.U8) return SymbolicValue.FromInteger(integers.Constant(0, signature.ElementType is ElementType.I8 or ElementType.U8));
			if (signature.IsValueType) return SymbolicValue.FromObject(type.FullName, address);
			return SymbolicValue.FromInteger(integers.Constant(0));
		}
		SymbolicValue Read(SymbolicState state, object address) {
			if (state.Memory.ContainsKey(address) || address is FlowAddress { Kind: "aggregate member" }) return state.Load(address);
			var value = address is FieldDef field ? field_value(field) : catalog.SlotValue(address);
			return state.ConservativeContext ? Uncertain(value, "storage value may have changed after incomplete value-flow analysis") : value;
		}
		static SymbolicValue Pop(SymbolicState state) { if (state.Stack.Count == 0) return SymbolicValue.Unknown; int index = state.Stack.Count - 1; var value = state.Stack[index]; state.Stack.RemoveAt(index); return value; }
	}
}
