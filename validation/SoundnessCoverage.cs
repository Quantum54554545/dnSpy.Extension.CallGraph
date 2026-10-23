using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class SoundnessCoverage {
    public static int Run() {
        using var module = new ModuleDefUser("Soundness.dll");
        new AssemblyDefUser("Soundness", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Validation", "Methods", module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(type);
        MethodDef Method(string name, MethodSig? signature = null) {
            var method = new MethodDefUser(name, signature ?? MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            type.Methods.Add(method); return method;
        }
        void Emit(MethodDef method, params Instruction[] instructions) {
            foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
            method.Body.UpdateInstructionOffsets();
        }
        CallGraphSlice Graph(MethodDef root) => CallGraphIndex.BuildReachable(root, new[] { module }, 32, 500, CallGraphFilterOptions.None).Slice;
        int passed = 0, failed = 0;
        void Assert(string name, bool success) {
            Console.WriteLine((success ? "PASS soundness " : "FAIL soundness ") + name);
            if (success) passed++; else failed++;
        }
        var a = Method("A"); Emit(a, Instruction.Create(OpCodes.Ret));
        var b = Method("B"); Emit(b, Instruction.Create(OpCodes.Ret));
        var field = new FieldDefUser("Pointer", new FieldSig(module.CorLibTypes.IntPtr), FieldAttributes.Public | FieldAttributes.Static);
        type.Fields.Add(field);
        var setter1 = Method("Setter1"); Emit(setter1, Instruction.Create(OpCodes.Ldftn, b), Instruction.Create(OpCodes.Stsfld, field), Instruction.Create(OpCodes.Ret));
        var setter2 = Method("Setter2"); Emit(setter2, Instruction.Create(OpCodes.Ldftn, b), Instruction.Create(OpCodes.Stsfld, field), Instruction.Create(OpCodes.Ret));
        var multiple = Method("MultipleCalliEffects", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        var select_b = Instruction.Create(OpCodes.Ldftn, setter2);
        var invoke = Instruction.Create(OpCodes.Calli, setter1.MethodSig);
        Emit(multiple, Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stsfld, field), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brtrue, select_b), Instruction.Create(OpCodes.Ldftn, setter1), Instruction.Create(OpCodes.Br, invoke), select_b, invoke, Instruction.Create(OpCodes.Ldsfld, field), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var multi_graph = Graph(multiple);
        Assert("all possible calli targets apply effects", multi_graph.Links.Any(link => link.Source == multiple && link.Target == b) && !multi_graph.Links.Any(link => link.Source == multiple && link.Target == a));

        var external_type = new TypeRefUser(module, "External", "Library", new AssemblyRefUser(new AssemblyNameInfo("Missing.Library, Version=1.0.0.0")));
        var external_ref = new MemberRefUser(module, "Replace", MethodSig.CreateStatic(module.CorLibTypes.Void, new ByRefSig(module.CorLibTypes.IntPtr)), external_type);
        var unresolved = Method("UnknownByRef"); var slot = new Local(module.CorLibTypes.IntPtr); unresolved.Body.Variables.Add(slot);
        Emit(unresolved, Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stloc, slot), Instruction.Create(OpCodes.Ldloca, slot), Instruction.Create(OpCodes.Call, external_ref), Instruction.Create(OpCodes.Ldloc, slot), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var unresolved_graph = Graph(unresolved);
        Assert("unresolved call cannot preserve exact ref pointer", !unresolved_graph.Links.Any(link => link.Source == unresolved && link.Target == a && !link.IsPotential) && unresolved_graph.UnresolvedIndirectCallCount != 0);

        var native = new MethodDefUser("NativeReplace", external_ref.MethodSig, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl);
        type.Methods.Add(native);
        var no_body = Method("NoBodyEffects"); var native_slot = new Local(module.CorLibTypes.IntPtr); no_body.Body.Variables.Add(native_slot);
        Emit(no_body, Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stloc, native_slot), Instruction.Create(OpCodes.Ldloca, native_slot), Instruction.Create(OpCodes.Call, native), Instruction.Create(OpCodes.Ldloc, native_slot), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var native_graph = Graph(no_body);
        Assert("bodyless callee cannot preserve exact ref pointer", !native_graph.Links.Any(link => link.Source == no_body && link.Target == a && !link.IsPotential) && native_graph.UnresolvedIndirectCallCount != 0);

        var unsafe_copy = Method("UnknownMemoryCopy", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.IntPtr));
        var copy_slot = new Local(module.CorLibTypes.IntPtr); unsafe_copy.Body.Variables.Add(copy_slot);
        Emit(unsafe_copy, Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stloc, copy_slot), Instruction.Create(OpCodes.Ldloca, copy_slot), Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(8), Instruction.Create(OpCodes.Cpblk), Instruction.Create(OpCodes.Ldloc, copy_slot), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var copy_graph = Graph(unsafe_copy);
        Assert("unmodeled memory write cannot preserve exact pointer", !copy_graph.Links.Any(link => link.Source == unsafe_copy && link.Target == a && !link.IsPotential) && copy_graph.UnresolvedIndirectCallCount != 0);

        var instance = new MethodDefUser("Run", MethodSig.CreateInstance(module.CorLibTypes.Void), MethodAttributes.Public) { Body = new CilBody() };
        type.Methods.Add(instance); Emit(instance, Instruction.Create(OpCodes.Call, b), Instruction.Create(OpCodes.Ret));
        var null_call = Method("NullReceiver");
        Emit(null_call, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Callvirt, instance), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        var null_graph = Graph(null_call);
        Assert("known null callvirt invokes neither body nor continuation", !null_graph.Links.Any(link => link.Target == a || link.Target == b || link.Target == instance));

        var throwing = Method("AlwaysThrows"); Emit(throwing, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw));
        var after_throw = Method("CalliThrowContinuation");
        Emit(after_throw, Instruction.Create(OpCodes.Ldftn, throwing), Instruction.Create(OpCodes.Calli, throwing.MethodSig), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Assert("throwing calli has no normal continuation", !Graph(after_throw).Links.Any(link => link.Source == after_throw && link.Target == a));
        var wrapper = Method("ThrowWrapper"); Emit(wrapper, Instruction.Create(OpCodes.Ldftn, throwing), Instruction.Create(OpCodes.Calli, throwing.MethodSig), Instruction.Create(OpCodes.Ret));
        var after_wrapper = Method("HelperCalliThrowContinuation"); Emit(after_wrapper, Instruction.Create(OpCodes.Call, wrapper), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Assert("throwing helper calli has no normal continuation", !Graph(after_wrapper).Links.Any(link => link.Source == after_wrapper && link.Target == a));

        var all_throw = Method("AllTargetsThrow", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
        var throwing2 = Method("AlsoThrows"); Emit(throwing2, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw));
        var alt_throw = Instruction.Create(OpCodes.Ldftn, throwing2); var throw_invoke = Instruction.Create(OpCodes.Calli, throwing.MethodSig);
        Emit(all_throw, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brtrue, alt_throw), Instruction.Create(OpCodes.Ldftn, throwing), Instruction.Create(OpCodes.Br, throw_invoke), alt_throw, throw_invoke, Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        Assert("all throwing indirect candidates terminate normal path", !Graph(all_throw).Links.Any(link => link.Source == all_throw && link.Target == a));

        var invalid = Method("InvalidDirectIndex"); Emit(invalid, Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        var dead_return = Method("AfterReturn"); Emit(dead_return, Instruction.Create(OpCodes.Ret), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        var dead_throw = Method("AfterThrowInstruction"); Emit(dead_throw, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        var never_catch = Method("UnreachableHandler");
        var try_start = Instruction.Create(OpCodes.Nop); var handler_start = Instruction.Create(OpCodes.Pop); var end = Instruction.Create(OpCodes.Ret);
        Emit(never_catch, try_start, Instruction.Create(OpCodes.Leave, end), handler_start, Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Leave, end), end);
        never_catch.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = try_start, TryEnd = handler_start, HandlerStart = handler_start, HandlerEnd = end, CatchType = module.CorLibTypes.Object.TypeDefOrRef });
        var opaque_helper = Method("BudgetedIndirectEffects"); Emit(opaque_helper, Instruction.Create(OpCodes.Ldftn, setter1), Instruction.Create(OpCodes.Calli, setter1.MethodSig), Instruction.Create(OpCodes.Ret));
        var budgeted = Method("UnknownHelperEffects"); Emit(budgeted, Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stsfld, field), Instruction.Create(OpCodes.Call, opaque_helper), Instruction.Create(OpCodes.Ldsfld, field), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var no_summary = new ReachableCallAnalysis(new[] { module }, CancellationToken.None);
        typeof(ReachableCallAnalysis).GetField("remaining_helper_steps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(no_summary, 0);
        var bounded_graph = no_summary.Select(budgeted, 32, 500, CallGraphFilterOptions.None);
        Assert("unfinished helper indirect writes invalidate exact caller state", !bounded_graph.Links.Any(link => link.Source == budgeted && link.Target == a && !link.IsPotential) && bounded_graph.UnresolvedIndirectCallCount != 0);
        var modifier = new TypeRefUser(module, "System.Runtime.CompilerServices", "IsExternalInit", module.CorLibTypes.AssemblyRef);
        var modified_void = Method("ModifiedVoid", MethodSig.CreateStatic(new CModReqdSig(modifier, module.CorLibTypes.Void))); Emit(modified_void, Instruction.Create(OpCodes.Ret));
        var initializer_root = Method("CallsAfterInitSetter"); Emit(initializer_root, Instruction.Create(OpCodes.Call, modified_void), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
        var modified_graph = Graph(initializer_root);
        Assert("modified void return does not push a phantom value", modified_graph.Links.Any(link => link.Source == initializer_root && link.Target == a) && !modified_graph.IsTruncated && !modified_graph.Diagnostics.Any(note => note.Kind == GraphDiagnosticKind.InvalidIl));
        var index = CallGraphIndex.Build(module);
        Assert("modified void return is valid in the reverse index", index.Outgoing(initializer_root).Any(link => link.Target == a));
        Assert("reverse index rejects stack-underflow call sites", !index.Outgoing(invalid).Any(link => link.Target == a));
        Assert("reverse index excludes instructions after return", !index.Outgoing(dead_return).Any(link => link.Target == a));
        Assert("reverse index excludes instructions after throw", !index.Outgoing(dead_throw).Any(link => link.Target == a));
        Assert("reverse index does not seed a handler without a throwing site", !index.Outgoing(never_catch).Any(link => link.Target == a));
        Console.WriteLine($"soundness_passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}
