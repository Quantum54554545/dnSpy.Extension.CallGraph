using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class TableFlowCoverage {
    public static int Run() {
        using var module = new ModuleDefUser("TableFlow.dll"); new AssemblyDefUser("TableFlow", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Validation", "Tables", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
        MethodDef Method(string name, MethodSig? signature = null, TypeDef? owner = null) {
            var method = new MethodDefUser(name, signature ?? MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() }; (owner ?? type).Methods.Add(method); return method;
        }
        void Emit(MethodDef method, params Instruction[] instructions) { foreach (var instruction in instructions) method.Body.Instructions.Add(instruction); method.Body.UpdateInstructionOffsets(); }
        var a = Method("A"); Emit(a, Instruction.Create(OpCodes.Ret)); var b = Method("B"); Emit(b, Instruction.Create(OpCodes.Ret));
        var external_type = new TypeRefUser(module, "Unknown", "Library", new AssemblyRefUser(new AssemblyNameInfo("Unknown.Library, Version=1.0.0.0")));
        var external = new MemberRefUser(module, "Opaque", MethodSig.CreateStatic(module.CorLibTypes.Void), external_type);
        var holder = new TypeDefUser("Validation", "Holder", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(holder);
        var table = new FieldDefUser("Pointers", new FieldSig(new SZArraySig(module.CorLibTypes.IntPtr)), FieldAttributes.Static | FieldAttributes.Public); holder.Fields.Add(table);
        var init = Method(".cctor", owner: holder); init.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        Emit(init, Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.Create(OpCodes.Stsfld, table), Instruction.Create(OpCodes.Ldsfld, table), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldsfld, table), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldftn, b), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ret));
        CallGraphSlice Graph(MethodDef root) => CallGraphIndex.BuildReachable(root, new[] { module }, 32, 500, CallGraphFilterOptions.None).Slice;
        int passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new Exception("Table-flow regression: " + name); Console.WriteLine("PASS tables " + name); passed++; }
        var exact = Method("ExactIndex"); Emit(exact, Instruction.Create(OpCodes.Ldsfld, table), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var graph = Graph(exact); Check("known initializer index excludes other addresses", graph.Links.Any(link => link.Source == exact && link.Target == b) && !graph.Links.Any(link => link.Source == exact && link.Target == a));
        var late = Method("LateTableRead"); Emit(late, Instruction.Create(OpCodes.Call, external), Instruction.Create(OpCodes.Ldsfld, table), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        graph = Graph(late); Check("opaque effects retain lazily initialized table candidates", graph.Links.Any(link => link.Source == late && link.Target == b && link.IsPotential) && graph.UnresolvedIndirectCallCount == 1);
        Check("known index does not expand every initializer entry", !graph.Links.Any(link => link.Source == late && link.Target == a));
        var stored = Method("MaterializedTable"); Emit(stored, Instruction.Create(OpCodes.Ldsfld, table), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Call, external), Instruction.Create(OpCodes.Ldsfld, table), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        graph = Graph(stored); Check("materialized table identity survives unknown effects only as a candidate", graph.Links.Any(link => link.Source == stored && link.Target == a && link.IsPotential) && graph.UnresolvedIndirectCallCount == 1);
        var address_only = Method("AddressTableOnly"); Emit(address_only, Instruction.Create(OpCodes.Ldsfld, table), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ret));
        Check("address initialization alone is not an invocation", !Graph(address_only).Links.Any(link => link.Source == address_only && (link.Target == a || link.Target == b)));
        var state = new SymbolicState(); var integers = new IntegerExpression.Factory(); var array_id = new object();
        var address = new FlowAddress("element", array_id, 0L); state.Store(address, SymbolicValue.FromMethod(a));
        ContextCallEvaluator.InvalidateUnknownCall(state, Array.Empty<SymbolicValue>());
        Check("opaque call does not mutate an unescaped local array", state.Load(address).Methods.Contains(a) && !state.Load(address).MayHaveUnknownTargets);
        ContextCallEvaluator.InvalidateUnknownCall(state, new[] { SymbolicValue.FromObject("System.IntPtr[]", array_id) });
        Check("passing array to opaque code invalidates exact element", state.Load(address).Methods.Contains(a) && state.Load(address).MayHaveUnknownTargets);
        state.Store(address, SymbolicValue.FromMethod(b)); ContextCallEvaluator.InvalidateUnknownCall(state, Array.Empty<SymbolicValue>());
        Check("previously escaped array remains externally mutable", state.Load(address).MayHaveUnknownTargets && state.Load(address).Methods.Contains(b));
        var alternatives = SymbolicValue.Merge(new[] { SymbolicValue.FromObject("System.IntPtr[]", new object()), SymbolicValue.FromObject("System.IntPtr[]", array_id) });
        Check("joining arrays retains finite object identities without an exact alias", alternatives.Storage is null && alternatives.StorageCandidates.Count == 2 && alternatives.IsAmbiguous);
        var memory = new FlowMemory(new PointerFlowCatalog(), _ => SymbolicValue.Unknown, integers);
        var merged_state = new SymbolicState(); foreach (var owner in alternatives.PossibleStorage) merged_state.Store(new FlowAddress("element", owner, 0L), SymbolicValue.FromMethod(Equals(owner, array_id) ? a : b));
        merged_state.Stack.Add(alternatives); merged_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(0))); memory.Execute(exact, Instruction.Create(OpCodes.Ldelem_I), merged_state, Array.Empty<SymbolicValue>());
        Check("array join reads both real candidate elements", merged_state.Stack.Single().Methods.SetEquals(new[] { a, b }));
        var stack_state = new SymbolicState(); stack_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(16))); memory.Execute(exact, Instruction.Create(OpCodes.Localloc), stack_state, Array.Empty<SymbolicValue>());
        var stack_pointer = stack_state.Stack.Single(); Check("localloc creates a distinct symbolic address", FlowMemory.IsAddress(stack_pointer));
        stack_state.Stack.Add(SymbolicValue.FromMethod(a)); memory.Execute(exact, Instruction.Create(OpCodes.Stind_I), stack_state, Array.Empty<SymbolicValue>()); stack_state.Stack.Add(stack_pointer); memory.Execute(exact, Instruction.Create(OpCodes.Ldind_I), stack_state, Array.Empty<SymbolicValue>());
        Check("same-address stack allocation pointer roundtrip", stack_state.Stack.Single().Methods.Contains(a));
        var c = Method("UnselectedC"); Emit(c, Instruction.Create(OpCodes.Ret));
        var partitioned = Method("FiniteIndices", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        var pointers = new Local(new SZArraySig(module.CorLibTypes.IntPtr)); var selector = new Local(module.CorLibTypes.Int32); partitioned.Body.Variables.Add(pointers); partitioned.Body.Variables.Add(selector);
        var one = Instruction.CreateLdcI4(1); var join = Instruction.Create(OpCodes.Stloc, selector);
        Emit(partitioned, Instruction.CreateLdcI4(3), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.Create(OpCodes.Stloc, pointers), Instruction.Create(OpCodes.Ldloc, pointers), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldloc, pointers), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldftn, b), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldloc, pointers), Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Ldftn, c), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brtrue, one), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Br, join), one, join, Instruction.Create(OpCodes.Ldloc, pointers), Instruction.Create(OpCodes.Ldloc, selector), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        graph = Graph(partitioned); Check("finite selector branches do not invent a third table target", graph.Links.Any(link => link.Source == partitioned && link.Target == a) && graph.Links.Any(link => link.Source == partitioned && link.Target == b) && !graph.Links.Any(link => link.Source == partitioned && link.Target == c));
        var byref_signature = MethodSig.CreateStatic(module.CorLibTypes.Void, Enumerable.Repeat<TypeSig>(new ByRefSig(module.CorLibTypes.Int32), 79).ToArray());
        var advance = Method("Advance79", byref_signature); Emit(advance, Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Stind_I4), Instruction.Create(OpCodes.Ret));
        var finish = Method("Finish79", byref_signature); Emit(finish, Instruction.Create(OpCodes.Ret));
        var wide = Method("WideByRefDispatcher"); var index_slot = new Local(module.CorLibTypes.Int32); var scratch = new Local(module.CorLibTypes.Int32); var wide_table = new Local(new SZArraySig(module.CorLibTypes.IntPtr)); wide.Body.Variables.Add(index_slot); wide.Body.Variables.Add(scratch); wide.Body.Variables.Add(wide_table);
        Emit(wide, Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Stloc, index_slot), Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.Create(OpCodes.Stloc, wide_table), Instruction.Create(OpCodes.Ldloc, wide_table), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldftn, advance), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldloc, wide_table), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldftn, finish), Instruction.Create(OpCodes.Stelem_I));
        for (int site = 0; site < 2; site++) { wide.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, index_slot)); for (int index = 1; index < 79; index++) wide.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloca, scratch)); foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldloc, wide_table), Instruction.Create(OpCodes.Ldloc, index_slot), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, byref_signature) }) wide.Body.Instructions.Add(instruction); }
        wide.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); wide.Body.UpdateInstructionOffsets(); graph = Graph(wide);
        Check("79 ref arguments preserve selector writes across indirect calls", graph.Links.Any(link => link.Source == wide && link.Target == advance) && graph.Links.Any(link => link.Source == wide && link.Target == finish) && graph.UnresolvedIndirectCallCount == 0);
        var invocations = new InvocationModel(new[] { module }, reference => reference as MethodDef, (_, _, _) => SymbolicValue.Unknown);
        var chars = new MemberRefUser(module, "get_Chars", MethodSig.CreateInstance(module.CorLibTypes.Char, module.CorLibTypes.Int32), module.CorLibTypes.GetTypeRef("System", "String"));
        var char_state = new SymbolicState(); invocations.TryEvaluate(chars, new[] { SymbolicValue.FromString("abc"), SymbolicValue.FromInteger(integers.Constant(1)) }, char_state, new object(), false, out var character);
        Check("immutable string indexing does not invalidate unrelated memory", character.Integer?.Value == 'b' && !char_state.ConservativeContext);
        invocations.TryEvaluate(chars, new[] { SymbolicValue.FromString("abc"), SymbolicValue.FromInteger(integers.Constant(3)) }, char_state, new object(), false, out _);
        Check("out-of-range constant string indexing terminates normal path", char_state.PathTerminated);
        var instance_field = new FieldDefUser("InstancePointer", new FieldSig(module.CorLibTypes.IntPtr), FieldAttributes.Public); type.Fields.Add(instance_field);
        var instance_state = new SymbolicState(); var left_owner = new object(); var right_owner = new object();
        instance_state.Store(new FlowAddress("field", left_owner, instance_field), SymbolicValue.FromMethod(a)); instance_state.Store(new FlowAddress("field", right_owner, instance_field), SymbolicValue.FromMethod(b));
        instance_state.Stack.Add(SymbolicValue.Merge(new[] { SymbolicValue.FromObject(type.FullName, left_owner), SymbolicValue.FromObject(type.FullName, right_owner) })); memory.Execute(exact, Instruction.Create(OpCodes.Ldfld, instance_field), instance_state, Array.Empty<SymbolicValue>());
        Check("field reads preserve both joined object candidates", instance_state.Stack.Single().Methods.SetEquals(new[] { a, b }));
        var overlap = new SymbolicState(); overlap.Store(stack_pointer.Storage!, SymbolicValue.FromMethod(a)); overlap.Store(FlowMemory.Offset(stack_pointer, 1, false).Storage!, SymbolicValue.FromInteger(integers.Constant(0)));
        Check("overlapping byte write invalidates an older whole pointer", overlap.Load(stack_pointer.Storage!).IsUnknown);
        overlap.Store(stack_pointer.Storage!, SymbolicValue.FromMethod(a)); ContextCallEvaluator.InvalidateUnknownCall(overlap, new[] { stack_pointer });
        Check("opaque write through a stack pointer cannot retain an exact target", overlap.Load(stack_pointer.Storage!).MayHaveUnknownTargets);
        var uncertain_virtual = invocations.ResolvePointer(SymbolicValue.Merge(new[] { SymbolicValue.FromMethod(a, true), SymbolicValue.Unknown }), new SymbolicState());
        Check("virtual pointer resolution preserves unknown remainder", uncertain_virtual.MayHaveUnknownTargets && uncertain_virtual.Methods.Contains(a));
        var cache_state = new SymbolicState(); cache_state.Store(new FlowAddress("length", array_id, 0), SymbolicValue.FromInteger(integers.Constant(1))); cache_state.Store(address, SymbolicValue.FromMethod(a));
        SymbolicValue LoadAll() { cache_state.Stack.Add(SymbolicValue.FromObject("System.IntPtr[]", array_id)); cache_state.Stack.Add(SymbolicValue.Unknown); memory.Execute(exact, Instruction.Create(OpCodes.Ldelem_I), cache_state, Array.Empty<SymbolicValue>()); var result = cache_state.Stack.Single(); cache_state.Stack.Clear(); return result; }
        Check("unknown-index array read starts with current contents", LoadAll().Methods.SetEquals(new[] { a })); cache_state.Store(address, SymbolicValue.FromMethod(b));
        Check("array-read cache observes subsequent exact writes", LoadAll().Methods.SetEquals(new[] { b }));
        var allocation_state = new SymbolicState(); var allocation_instruction = Instruction.Create(OpCodes.Localloc);
        allocation_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(8))); memory.Execute(exact, allocation_instruction, allocation_state, Array.Empty<SymbolicValue>()); var old_stack = allocation_state.Stack.Single(); allocation_state.Stack.Clear();
        allocation_state.Store(old_stack.Storage!, SymbolicValue.FromMethod(a)); allocation_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(8))); memory.Execute(exact, allocation_instruction, allocation_state, Array.Empty<SymbolicValue>());
        Check("repeated stack allocation does not reuse the previous exact pointer", allocation_state.Stack.Single().IsUnknown && allocation_state.Load(old_stack.Storage!).Methods.Contains(a));
        var array_state = new SymbolicState(); var new_array = Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef);
        array_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(1))); memory.Execute(exact, new_array, array_state, Array.Empty<SymbolicValue>()); var old_array = array_state.Stack.Single(); array_state.Stack.Clear(); array_state.Store(new FlowAddress("element", old_array.Storage!, 0L), SymbolicValue.FromMethod(a));
        array_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(1))); memory.Execute(exact, new_array, array_state, Array.Empty<SymbolicValue>()); var next_array = array_state.Stack.Single();
        Check("repeated array allocation separates old object from uncertain summary", next_array.MayHaveUnknownTargets && !Equals(next_array.Storage, old_array.Storage));
        var invalid_read = Method("OutOfBoundsRead"); Emit(invalid_read, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Check("known out-of-bounds read excludes normal continuation", !Graph(invalid_read).Links.Any(link => link.Source == invalid_read && link.Target == a));
        var invalid_store = Method("OutOfBoundsStore"); Emit(invalid_store, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.CreateLdcI4(-1), Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Check("negative-index store cannot manufacture a reachable call", !Graph(invalid_store).Links.Any(link => link.Source == invalid_store && link.Target == a));
        var null_array = Method("NullArrayRead"); Emit(null_array, Instruction.Create(OpCodes.Ldnull), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Check("known null array excludes normal continuation", !Graph(null_array).Links.Any(link => link.Source == null_array && link.Target == a));
        var third_state = new SymbolicState(); third_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(1))); memory.Execute(exact, new_array, third_state, Array.Empty<SymbolicValue>()); third_state.Stack.Clear();
        third_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(2))); memory.Execute(exact, new_array, third_state, Array.Empty<SymbolicValue>()); var summary_array = third_state.Stack.Single(); third_state.Stack.Clear();
        third_state.Stack.Add(SymbolicValue.FromInteger(integers.Constant(3))); memory.Execute(exact, new_array, third_state, Array.Empty<SymbolicValue>());
        Check("repeated summary arrays do not claim a single exact length", third_state.Load(new FlowAddress("length", summary_array.Storage!, 0)).Integer is null);
        var get_module = new MemberRefUser(module, "get_Module", MethodSig.CreateInstance(new ClassSig(module.CorLibTypes.GetTypeRef("System.Reflection", "Module"))), module.CorLibTypes.GetTypeRef("System", "Type"));
        Check("runtime Type descriptor can identify its module without executing input", invocations.TryEvaluate(get_module, new[] { SymbolicValue.FromType(type.FullName) }, new SymbolicState(), new object(), false, out var runtime_module) && runtime_module.Storage is FlowAddress { Kind: "runtime module" });
        Check("ordinary/custom Type object is not mistaken for a runtime descriptor", !invocations.TryEvaluate(get_module, new[] { SymbolicValue.FromObject(type.FullName, new object()) }, new SymbolicState(), new object(), false, out _));
        Console.WriteLine("tables_passed=" + passed); return 0;
    }
}
