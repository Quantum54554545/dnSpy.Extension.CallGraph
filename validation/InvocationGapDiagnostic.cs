using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class InvocationGapDiagnostic {
    public static int Run() {
        using var module = new ModuleDefUser("InvocationDiagnostics.dll");
        new AssemblyDefUser("InvocationDiagnostics", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Diagnostic", "Cases", module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(type);
        MethodDef Method(string name, MethodSig? signature = null) {
            var method = new MethodDefUser(name, signature ?? MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            type.Methods.Add(method);
            return method;
        }
        void Emit(MethodDef method, params Instruction[] instructions) {
            foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
            method.Body.UpdateInstructionOffsets();
        }
        var target = Method("Target");
        Emit(target, Instruction.Create(OpCodes.Ret));
        var signature = MethodSig.CreateStatic(module.CorLibTypes.Void);
        void Check(MethodDef root, bool expected) {
            var graph = CallGraphIndex.BuildReachable(root, new[] { module }, 10, 100, CallGraphFilterOptions.None).Slice;
            bool actual = graph.Links.Any(link => link.Source == root && link.Target == target);
            Console.WriteLine($"{(actual == expected ? "OK" : "GAP")} {root.Name} expected_invocation={expected} found={actual} unknown_calli={graph.UnresolvedIndirectCallCount}");
            if(actual!=expected)throw new Exception("Invocation gap regression: "+root.Name);
        }
        var direct = Method("DirectCall");
        Emit(direct, Instruction.Create(OpCodes.Call, target), Instruction.Create(OpCodes.Ret));
        Check(direct, true);
        var direct_pointer = Method("DirectPointer");
        Emit(direct_pointer, Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));
        Check(direct_pointer, true);
        var reference = Method("AddressOnly", MethodSig.CreateStatic(module.CorLibTypes.IntPtr));
        Emit(reference, Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Ret));
        Check(reference, false);
        var box = new TypeDefUser("Diagnostic", "PointerBox", module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(box);
        var field = new FieldDefUser("Pointer", new FieldSig(module.CorLibTypes.IntPtr), FieldAttributes.Public);
        box.Fields.Add(field);
        var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) { Body = new CilBody() };
        box.Methods.Add(ctor);
        Emit(ctor, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), module.CorLibTypes.Object.TypeDefOrRef)), Instruction.Create(OpCodes.Ret));
        var instance = Method("PointerThroughInstanceField");
        Emit(instance, Instruction.Create(OpCodes.Newobj, ctor), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Stfld, field), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));
        Check(instance, true);
        var byref = Method("PointerThroughLocalAddress");
        var local = new Local(module.CorLibTypes.IntPtr);
        byref.Body.Variables.Add(local);
        Emit(byref, Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Stloc, local), Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Ldind_I), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));
        Check(byref, true);
        var conversion = Method("PointerThroughInt64");
        Emit(conversion, Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Conv_I8), Instruction.Create(OpCodes.Conv_I), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));
        Check(conversion, true);
        var jump = Method("JumpToMethod");
        Emit(jump, Instruction.Create(OpCodes.Jmp, target));
        Check(jump, true);
        var helper = Method("LargePointerHelper", MethodSig.CreateStatic(module.CorLibTypes.IntPtr));
        for (int i = 0; i < 2050; i++) helper.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
        Emit(helper, Instruction.Create(OpCodes.Ldftn, target), Instruction.Create(OpCodes.Ret));
        var large = Method("PointerThroughLargeHelper");
        Emit(large, Instruction.Create(OpCodes.Call, helper), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));
        Check(large, true);
        var tail = Method("TailCallPointer");
        Emit(tail, Instruction.Create(OpCodes.Ldftn,target),Instruction.Create(OpCodes.Tailcall),Instruction.Create(OpCodes.Calli,signature),Instruction.Create(OpCodes.Ret));Check(tail,true);
        var mismatch=Method("MismatchedSignature");
        Emit(mismatch,Instruction.CreateLdcI4(1),Instruction.Create(OpCodes.Ldftn,target),Instruction.Create(OpCodes.Calli,MethodSig.CreateStatic(module.CorLibTypes.Int32,module.CorLibTypes.Int32)),Instruction.Create(OpCodes.Pop),Instruction.Create(OpCodes.Ret));Check(mismatch,false);
        var base_type=new TypeDefUser("Diagnostic","Base",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(base_type);
        var derived_type=new TypeDefUser("Diagnostic","Derived",base_type);module.Types.Add(derived_type);
        var base_method=new MethodDefUser("Virtual",MethodSig.CreateInstance(module.CorLibTypes.Void),MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot){Body=new CilBody()};base_type.Methods.Add(base_method);Emit(base_method,Instruction.Create(OpCodes.Ret));
        var override_method=new MethodDefUser("Virtual",MethodSig.CreateInstance(module.CorLibTypes.Void),MethodAttributes.Public|MethodAttributes.Virtual){Body=new CilBody()};derived_type.Methods.Add(override_method);Emit(override_method,Instruction.Create(OpCodes.Call,target),Instruction.Create(OpCodes.Ret));
        var derived_ctor=new MethodDefUser(".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void),MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.RTSpecialName){Body=new CilBody()};derived_type.Methods.Add(derived_ctor);Emit(derived_ctor,Instruction.Create(OpCodes.Ret));
        var virtual_root=Method("VirtualPointer");Emit(virtual_root,Instruction.Create(OpCodes.Newobj,derived_ctor),Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Ldvirtftn,base_method),Instruction.Create(OpCodes.Calli,MethodSig.CreateInstance(module.CorLibTypes.Void)),Instruction.Create(OpCodes.Ret));
        var virtual_slice=CallGraphIndex.BuildReachable(virtual_root,new[]{module},10,100,CallGraphFilterOptions.None).Slice;
        if(!virtual_slice.Links.Any(link=>link.Source==virtual_root&&link.Target==override_method&&!link.IsPotential)||virtual_slice.Links.Any(link=>link.Source==virtual_root&&link.Target==base_method))throw new Exception("ldvirtftn must select the known receiver override");
        Console.WriteLine("PASS ldvirtftn resolves the known receiver override");
        return 0;
    }
}
