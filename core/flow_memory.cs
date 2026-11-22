using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.CallGraph.Core {
	sealed record flow_address(string kind, object owner, object slot);

	sealed class flow_memory {
		readonly pointer_flow_catalog catalog;
		readonly Func<FieldDef?, symbolic_value> field_value;
		readonly integer_expression.factory integers;
		sealed record array_read((long index, symbolic_value value, bool catalog)[] entries, symbolic_value length, bool conservative, symbolic_value result);
		readonly Dictionary<object, array_read> array_reads = new Dictionary<object, array_read>();
		public flow_memory(pointer_flow_catalog catalog, Func<FieldDef?, symbolic_value> field_value, integer_expression.factory integers) {
			this.catalog = catalog; this.field_value = field_value; this.integers = integers;
		}
		public static flow_address local(symbolic_state state, int index) => new flow_address("local", state.frame, index);
		public static flow_address argument(symbolic_state state, int index) => new flow_address("argument", state.frame, index);
		public static symbolic_value read_object(symbolic_state state, symbolic_value value) {
			if (value.storage is flow_address address && (state.memory.ContainsKey(address) || address.kind == "aggregate member")) return state.load(address);
			if (value.type_names.Count == 0 && value.storage is null && value.storage_candidates.Count != 0 && value.storage_candidates.All(address => address is flow_address && state.memory.ContainsKey(address))) return symbolic_value.merge(value.storage_candidates.Select(state.load).Concat(value.may_have_unknown_targets ? new[] { symbolic_value.unknown } : Array.Empty<symbolic_value>()));
			return value;
		}
		public static bool is_address(symbolic_value value) => value.storage is not null && value.type_names.Count == 0 && !value.is_boxed && !value.is_ambiguous;
		public static object? address_root(object? address) { while (address is flow_address { kind: "byte offset" } offset) address = offset.owner; return address; }
		public static symbolic_value offset(symbolic_value pointer, long amount, bool subtract) {
			try {
				long offset = subtract ? checked(-amount) : amount; object owner = pointer.storage!;
				if (owner is flow_address { kind: "byte offset", slot: long previous } address) { offset = checked(offset + previous); owner = address.owner; }
				return symbolic_value.from_storage(offset == 0 ? owner : new flow_address("byte offset", owner, offset));
			}
			catch (OverflowException) { return symbolic_value.unknown_because("address offset overflow"); }
		}
		public bool execute(MethodDef method, Instruction instruction, symbolic_state state, IReadOnlyList<symbolic_value> arguments) {
			var code = instruction.OpCode.Code;
			if (code == Code.Localloc) {
				var size = flow_memory.pop(state); var allocation = new flow_address("stack allocation", state.frame, instruction);
				if (state.memory.ContainsKey(new flow_address("length", allocation, 0))) { state.stack.Add(symbolic_value.unknown_because("repeated stack allocation site has no exact address summary")); return true; }
				state.store(new flow_address("length", allocation, 0), size);
				state.stack.Add(symbolic_value.from_storage(allocation)); return true;
			}
			if (code is Code.Ldloca or Code.Ldloca_S && instruction.Operand is Local local) {
				var address = flow_memory.local(state, local.Index);
				if (!state.memory.ContainsKey(address)) state.store(address, state.locals.TryGetValue(local.Index, out var value) ? value : this.initial_local(method, local, address));
				state.stack.Add(symbolic_value.from_storage(address)); return true;
			}
			if (code is Code.Ldarga or Code.Ldarga_S && instruction.Operand is Parameter parameter) {
				var address = flow_memory.argument(state, parameter.Index);
				if (!state.memory.ContainsKey(address)) state.store(address, state.arguments.TryGetValue(parameter.Index, out var value) ? value : parameter.Index < arguments.Count ? arguments[parameter.Index] : symbolic_value.unknown);
				state.stack.Add(symbolic_value.from_storage(address)); return true;
			}
			if (code is Code.Starg or Code.Starg_S && instruction.Operand is Parameter destination) {
				var value = flow_memory.pop(state); state.arguments[destination.Index] = value; state.store(flow_memory.argument(state, destination.Index), value); return true;
			}
			if (code is Code.Ldsfld or Code.Ldsflda or Code.Stsfld) {
				var field = this.catalog.resolve_field(instruction.Operand as IField);
				if (field is null) { if (code == Code.Stsfld) flow_memory.pop(state); else state.stack.Add(symbolic_value.unknown); return true; }
				var initial = this.field_value(field);
				if (state.conservative_context && !field.IsLiteral) initial = flow_memory.uncertain(initial, "field value may have changed after incomplete value-flow analysis");
				if (code == Code.Stsfld) state.store(field, flow_memory.pop(state));
				else if (code == Code.Ldsflda) { if (!field.HasFieldRVA && !state.memory.ContainsKey(field)) state.store(field, initial.is_unknown ? symbolic_value.opaque(new flow_address("unknown field value", field, state.memory_stamp)) : initial); state.stack.Add(symbolic_value.from_storage(field)); }
				else {
					var value = state.memory.TryGetValue(field, out var stored) ? stored : initial;
					if (value.is_unknown) { value = symbolic_value.opaque(new flow_address("unknown field value", field, state.memory_stamp)); state.store(field, value); }
					else if (!state.memory.ContainsKey(field)) state.store(field, value);
					state.stack.Add(value);
				}
				return true;
			}
			if (code is Code.Ldfld or Code.Ldflda or Code.Stfld) {
				var value = code == Code.Stfld ? flow_memory.pop(state) : symbolic_value.unknown;
				var raw_instance = flow_memory.pop(state);
				var instance = flow_memory.read_object(state, raw_instance);
				if (context_call_evaluator.is_definitely_null(instance)) { state.path_terminated = true; return true; }
				if (instruction.Operand is IField aggregate_field && instance.members.TryGetValue(aggregate_field.Name, out var member)) {
					if (code == Code.Ldfld) state.stack.Add(member);
					else if (code == Code.Stfld && raw_instance.storage is object location) state.store(location, instance.with_member(aggregate_field.Name, value));
					else if (code == Code.Ldflda && raw_instance.storage is object location_address) {
						var member_address = new flow_address("aggregate member", location_address, aggregate_field.Name.ToString());
						state.store(member_address, member); state.stack.Add(symbolic_value.from_storage(member_address));
					}
					else if (code == Code.Ldflda) state.stack.Add(symbolic_value.unknown);
					return true;
				}
				var field = this.catalog.resolve_field(instruction.Operand as IField);
				if (code == Code.Ldfld && instruction.Operand is IField reference && reference.Name == "Target" && instance.dynamic_names.Count != 0 && reference.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.CallSite`", StringComparison.Ordinal)) { state.stack.Add(instance); return true; }
				object? address = instance.storage is not null && field is not null ? new flow_address("field", instance.storage, field) : null;
				if (code == Code.Stfld) {
					if (!instance.is_exact_type && field is not null) foreach (var key in new List<object>(state.memory.Keys)) if (key is flow_address slot && slot.kind == "field" && Equals(slot.slot, field) && !Equals(slot, address)) state.store(key, symbolic_value.unknown);
					if (field is not null) foreach (var owner in instance.possible_storage) { var location = new flow_address("field", owner, field); state.store(location, address is not null && !instance.is_ambiguous && !instance.may_have_unknown_targets ? value : symbolic_value.merge(new[] { this.read(state, location), value })); }
				}
				else state.stack.Add(field is null ? symbolic_value.unknown : flow_memory.read_candidates(instance, owner => code == Code.Ldflda ? symbolic_value.from_storage(new flow_address("field", owner, field)) : this.read(state, new flow_address("field", owner, field))));
				return true;
			}
			if (code == Code.Newarr) {
				var length = flow_memory.pop(state);
				object identity = new flow_address("allocation", state.frame, instruction);
				bool repeated = state.memory.ContainsKey(new flow_address("length", identity, 0));
				if (repeated) identity = new flow_address("summary allocation", state.frame, instruction);
				var length_slot = new flow_address("length", identity, 0);
				state.store(length_slot, repeated && state.memory.ContainsKey(length_slot) ? symbolic_value.merge(new[] { state.load(length_slot), length }) : length);
				string array_type = (instruction.Operand as ITypeDefOrRef)?.FullName + "[]";
				state.stack.Add(repeated ? symbolic_value.from_reference(array_type, identity, false) : symbolic_value.from_object(array_type, identity)); return true;
			}
			if (code == Code.Ldlen) {
				var array = flow_memory.pop(state); if (context_call_evaluator.is_definitely_null(array)) state.path_terminated = true;
				state.stack.Add(flow_memory.read_candidates(array, owner => this.read(state, new flow_address("length", owner, 0)))); return true;
			}
			if (code is Code.Stelem or Code.Stelem_I or Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_Ref) {
				var value = flow_memory.pop(state); var index = flow_memory.pop(state).integer; var array_value = flow_memory.pop(state);
				if (this.array_access_fails(state, array_value, index)) { state.path_terminated = true; return true; }
				value = this.narrow(code, value);
				foreach (var array in array_value.possible_storage) {
					if (this.invalid_index(state, array, index)) continue;
					if (index?.is_constant == true) { var address = new flow_address("element", array, index.value); state.store(address, array_value.storage is not null && !array_value.is_ambiguous && !array_value.may_have_unknown_targets ? value : symbolic_value.merge(new[] { this.read(state, address), value })); }
					else {
						var addresses = this.catalog.array_elements_value(array).Select(pair => (object)new flow_address("element", array, pair.index)).Concat(state.memory.Keys.Where(key => key is flow_address slot && slot.kind == "element" && Equals(slot.owner, array))).Distinct().Take(512).ToArray();
						foreach (var address in addresses) state.store(address, symbolic_value.merge(new[] { this.read(state, address), value }));
					}
				}
				return true;
			}
			if (code is Code.Ldelem or Code.Ldelem_I or Code.Ldelem_I1 or Code.Ldelem_U1 or Code.Ldelem_I2 or Code.Ldelem_U2 or Code.Ldelem_I4 or Code.Ldelem_U4 or Code.Ldelem_I8 or Code.Ldelem_Ref or Code.Ldelema) {
				var index = flow_memory.pop(state).integer; var array_value = flow_memory.pop(state);
				if (this.array_access_fails(state, array_value, index)) { state.path_terminated = true; state.stack.Add(symbolic_value.unknown); return true; }
				var element = flow_memory.read_candidates(array_value, array => this.read_element(state, array, index, code == Code.Ldelema));
				state.stack.Add(code == Code.Ldelema ? element : this.narrow(code, element)); return true;
			}
			if (code is Code.Ldind_I or Code.Ldind_I1 or Code.Ldind_U1 or Code.Ldind_I2 or Code.Ldind_U2 or Code.Ldind_I4 or Code.Ldind_U4 or Code.Ldind_I8 or Code.Ldind_Ref or Code.Ldobj) {
				var address = flow_memory.pop(state); state.stack.Add(flow_memory.read_candidates(address, candidate => this.read_rva(method, state, candidate, code) ?? this.narrow(code, this.read(state, candidate)))); return true;
			}
			if (code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_Ref or Code.Stobj) {
				var value = this.narrow(code, flow_memory.pop(state)); var address = flow_memory.pop(state);
				foreach (var candidate in address.possible_storage) state.store(candidate, address.storage is not null && !address.is_ambiguous ? value : symbolic_value.merge(new[] { this.read(state, candidate), value })); return true;
			}
			if (code == Code.Cpobj) {
				var source = flow_memory.pop(state); var copy_destination = flow_memory.pop(state); var value = flow_memory.read_candidates(source, address => this.read(state, address));
				foreach (var address in copy_destination.possible_storage) state.store(address, copy_destination.storage is not null && !copy_destination.is_ambiguous ? value : symbolic_value.merge(new[] { this.read(state, address), value })); return true;
			}
			if (code == Code.Initobj) {
				var init_destination = flow_memory.pop(state);
				foreach (var address in init_destination.possible_storage) { var value = this.initial(instruction.Operand as ITypeDefOrRef, address); state.store(address, init_destination.storage is not null && !init_destination.is_ambiguous ? value : symbolic_value.merge(new[] { this.read(state, address), value })); } return true;
			}
			return false;
		}
		static symbolic_value uncertain(symbolic_value value, string reason) => symbolic_value.merge(new[] { value, symbolic_value.unknown_because(reason) });
		bool invalid_index(symbolic_state state, object array, integer_expression? index) {
			if (index?.is_constant != true) return false;
			if (index.value < 0) return true;
			var address = new flow_address("length", array, 0);
			var length = (state.memory.ContainsKey(address) ? state.load(address) : this.catalog.slot_value(address)).integer;
			return length?.is_constant == true && index.value >= length.value;
		}
		bool array_access_fails(symbolic_state state, symbolic_value array, integer_expression? index) => context_call_evaluator.is_definitely_null(array) || index?.is_constant == true && (index.value < 0 || !array.may_have_unknown_targets && array.possible_storage.Any() && array.possible_storage.All(owner => this.invalid_index(state, owner, index)));
		static symbolic_value read_candidates(symbolic_value pointer, Func<object, symbolic_value> read) {
			var values = pointer.possible_storage.Select(read);
			return symbolic_value.merge(pointer.may_have_unknown_targets || pointer.is_unknown ? values.Append(symbolic_value.unknown_because(pointer.unknown_reason ?? "storage identity is incomplete")) : values);
		}
		symbolic_value read_element(symbolic_state state, object array, integer_expression? index, bool address_only) {
			if (this.invalid_index(state, array, index)) return symbolic_value.unknown_because("array index is outside this candidate object's bounds");
			if (index?.is_constant == true) {
				var address = new flow_address("element", array, index.value);
				if (address_only) return symbolic_value.from_storage(address);
				return state.memory.TryGetValue(address, out var value) ? value : !this.catalog.slot_value(address).is_unknown ? this.read(state, address) : this.catalog.element_value(array, index);
			}
			if (address_only) return symbolic_value.unknown;
			var elements = this.catalog.array_elements_value(array).Take(512).ToDictionary(pair => pair.index, pair => (value: pair.value, catalog: true));
			foreach (var pair in state.memory.Where(pair => pair.Key is flow_address slot && slot.kind == "element" && Equals(slot.owner, array))) elements[(long)((flow_address)pair.Key).slot] = (value: pair.Value, false);
			var entries = elements.OrderBy(pair => pair.Key).Take(512).Select(pair => (index: pair.Key, value: pair.Value.value, catalog: pair.Value.catalog)).ToArray();
			var length_value = this.read(state, new flow_address("length", array, 0));
			if (this.array_reads.TryGetValue(array, out var cached) && cached.conservative == state.conservative_context && cached.length.same_as(length_value) && entries.Length == cached.entries.Length && entries.Zip(cached.entries, (left, right) => left.index == right.index && left.catalog == right.catalog && ReferenceEquals(left.value, right.value)).All(same => same)) return cached.result;
			var values = entries.Select(pair => pair.catalog && state.conservative_context ? flow_memory.uncertain(pair.value, "array contents may have changed") : pair.value).ToArray();
			var length = length_value.integer;
			var result = symbolic_value.merge(length?.is_constant == true && length.value == values.Length ? values : values.Append(symbolic_value.unknown_because("array contents are incomplete")));
			if (this.array_reads.Count >= 4096) this.array_reads.Clear();
			this.array_reads[array] = new array_read(entries, length_value, state.conservative_context, result);
			return result;
		}
		symbolic_value? read_rva(MethodDef method, symbolic_state state, object address, Code code) {
			if (flow_memory.address_root(address) is not FieldDef field || !field.HasFieldRVA) return null;
			uint size = field.GetFieldSize();
			if (size == 0 || size > 1048576) return symbolic_value.unknown_because("RVA data size is outside the supported read limit");
			if (field.InitialValue is not byte[] data) return symbolic_value.unknown_because("RVA data is unavailable");
			if (method.Module.Machine is not dnlib.PE.Machine.I386 and not dnlib.PE.Machine.AMD64) return symbolic_value.unknown_because("RVA data endianness is not modeled for this architecture");
			if (state.memory.Keys.Any(key => Equals(flow_memory.address_root(key), field))) return symbolic_value.unknown_because("RVA storage may have been modified");
			long offset = address is flow_address { kind: "byte offset", slot: long value } ? value : 0;
			int width = code switch { Code.Ldind_I1 or Code.Ldind_U1 => 1, Code.Ldind_I2 or Code.Ldind_U2 => 2, Code.Ldind_I4 or Code.Ldind_U4 => 4, Code.Ldind_I8 => 8, Code.Ldind_I => method.Module.GetPointerSize(0), _ => 0 };
			if (width == 0 || offset < 0 || offset > data.Length - width) return symbolic_value.unknown_because("RVA read width or offset is outside modeled data");
			var bytes = data.AsSpan((int)offset, width);
			long number = width switch { 1 => code == Code.Ldind_I1 ? (sbyte)bytes[0] : bytes[0], 2 => code == Code.Ldind_I2 ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes), 4 => BinaryPrimitives.ReadInt32LittleEndian(bytes), _ => BinaryPrimitives.ReadInt64LittleEndian(bytes) };
			return symbolic_value.from_integer(this.integers.constant(number, width == 8));
		}
		symbolic_value narrow(Code code, symbolic_value value) {
			var conversion = code switch {
				Code.Stelem_I1 or Code.Stind_I1 or Code.Ldelem_I1 or Code.Ldind_I1 => Code.Conv_I1,
				Code.Ldelem_U1 or Code.Ldind_U1 => Code.Conv_U1,
				Code.Stelem_I2 or Code.Stind_I2 or Code.Ldelem_I2 or Code.Ldind_I2 => Code.Conv_I2,
				Code.Ldelem_U2 or Code.Ldind_U2 => Code.Conv_U2,
				Code.Stelem_I4 or Code.Stind_I4 or Code.Ldelem_I4 or Code.Ldind_I4 or Code.Ldelem_U4 or Code.Ldind_U4 => Code.Conv_I4,
				_ => Code.Nop,
			};
			return conversion == Code.Nop ? value : symbolic_value.from_integer(this.integers.convert(conversion, value.integer));
		}
		internal symbolic_value initial_local(MethodDef method, Local local, object address) {
			if (!method.Body.InitLocals) return symbolic_value.unknown;
			var signature = local.Type.RemovePinnedAndModifiers();
			if (signature.ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SZArray or ElementType.Array) return symbolic_value.from_integer(this.integers.constant(0));
			if (signature.ElementType == ElementType.ValueType) return symbolic_value.from_object(signature.FullName, address);
			return this.initial(signature.ToTypeDefOrRef(), address);
		}
		symbolic_value initial(ITypeDefOrRef? type, object address) {
			if (type is null) return symbolic_value.unknown;
			var signature = type.ToTypeSig();
			if (signature.ElementType is ElementType.Boolean or ElementType.Char or ElementType.I1 or ElementType.U1 or ElementType.I2 or ElementType.U2 or ElementType.I4 or ElementType.U4 or ElementType.I or ElementType.U or ElementType.I8 or ElementType.U8) return symbolic_value.from_integer(this.integers.constant(0, signature.ElementType is ElementType.I8 or ElementType.U8));
			if (signature.IsValueType) return symbolic_value.from_object(type.FullName, address);
			return symbolic_value.from_integer(this.integers.constant(0));
		}
		symbolic_value read(symbolic_state state, object address) {
			if (state.memory.ContainsKey(address) || address is flow_address { kind: "aggregate member" }) return state.load(address);
			var value = address is FieldDef field ? this.field_value(field) : this.catalog.slot_value(address);
			return state.conservative_context ? flow_memory.uncertain(value, "storage value may have changed after incomplete value-flow analysis") : value;
		}
		static symbolic_value pop(symbolic_state state) { if (state.stack.Count == 0) return symbolic_value.unknown; int index = state.stack.Count - 1; var value = state.stack[index]; state.stack.RemoveAt(index); return value; }
	}
}
