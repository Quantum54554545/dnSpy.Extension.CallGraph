using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class ObfuscationCoverage {
    public static int Run() {
        using var module = new ModuleDefUser("ObfuscationCases.dll"); new AssemblyDefUser("ObfuscationCases", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Validation", "Methods", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
        MethodDef Method(string name, TypeDef? owner = null) { var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() }; (owner ?? type).Methods.Add(method); return method; }
        void Emit(MethodDef method, params Instruction[] body) { foreach (var instruction in body) method.Body.Instructions.Add(instruction); method.Body.UpdateInstructionOffsets(); }
        CallGraphSlice Graph(MethodDef root) => CallGraphIndex.BuildReachable(root, new[] { module }, 32, 100, CallGraphFilterOptions.None).Slice;
        int passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new Exception("Obfuscation regression: " + name); passed++; Console.WriteLine("PASS obfuscation " + name); }
        var a = Method("A"); Emit(a, Instruction.Create(OpCodes.Ret));
        var b = Method("B"); Emit(b, Instruction.Create(OpCodes.Ret));
        var action = module.CorLibTypes.GetTypeRef("System", "Action");
        var invoke = new MemberRefUser(module, "Invoke", MethodSig.CreateInstance(module.CorLibTypes.Void), action);
        var external = new TypeRefUser(module, "External", "Initializer", new AssemblyRefUser(new AssemblyNameInfo("Unavailable, Version=1.0.0.0")));
        var populate = new MemberRefUser(module, "PopulateFields", MethodSig.CreateStatic(module.CorLibTypes.Void), external);
        var holder = new TypeDefUser("Validation", "Holder", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(holder);
        var field = new FieldDefUser("Callback", new FieldSig(new ClassSig(action)), FieldAttributes.Static | FieldAttributes.Public); holder.Fields.Add(field);
        var cctor = Method(".cctor", holder); cctor.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        Emit(cctor, Instruction.Create(OpCodes.Call, populate), Instruction.Create(OpCodes.Ret));
        var getter = Method("GetCallback", holder); getter.MethodSig.RetType = new ClassSig(action); Emit(getter, Instruction.Create(OpCodes.Ldsfld, field), Instruction.Create(OpCodes.Ret));
        var root = Method("UnknownInitializer"); Emit(root, Instruction.Create(OpCodes.Call, getter), Instruction.Create(OpCodes.Callvirt, invoke), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret)); module.EntryPoint = root;
        var graph = Graph(root);
        Check("opaque initializer cannot prove a delegate field is null", graph.Links.Any(link => link.Source == root && link.Target == a) && graph.Diagnostics.Any(note => note.Kind == GraphDiagnosticKind.UnresolvedDelegate));
        var empty = Method("EmptyBody"); var empty_graph = Graph(empty);
        Check("empty body is an incomplete boundary, not a complete leaf", empty_graph.IsTruncated && empty_graph.Diagnostics.Any(note => note.Kind == GraphDiagnosticKind.UnsupportedInstruction));
        module.Machine = dnlib.PE.Machine.AMD64;
        var data = new FieldDefUser("Data", new FieldSig(module.CorLibTypes.Int64), FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.HasFieldRVA) { InitialValue = new byte[] { 255, 0, 0, 0, 1, 0, 0, 0 } }; type.Fields.Add(data);
        var table_root = Method("RvaIndex"); var table = new Local(new SZArraySig(module.CorLibTypes.IntPtr)); table_root.Body.Variables.Add(table);
        Emit(table_root, Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Newarr, module.CorLibTypes.IntPtr.TypeDefOrRef), Instruction.Create(OpCodes.Stloc, table), Instruction.Create(OpCodes.Ldloc, table), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Ldftn, a), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldloc, table), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Ldftn, b), Instruction.Create(OpCodes.Stelem_I), Instruction.Create(OpCodes.Ldloc, table), Instruction.Create(OpCodes.Ldsflda, data), Instruction.Create(OpCodes.Conv_U), Instruction.CreateLdcI4(4), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ldind_I4), Instruction.Create(OpCodes.Ldelem_I), Instruction.Create(OpCodes.Calli, a.MethodSig), Instruction.Create(OpCodes.Ret));
        var table_graph = Graph(table_root);
        Check("RVA index and address conversion select the correct pointer", table_graph.Links.Any(link => link.Source == table_root && link.Target == b) && !table_graph.Links.Any(link => link.Source == table_root && link.Target == a));
        var integers = new IntegerExpression.Factory(); var memory = new FlowMemory(new PointerFlowCatalog(), _ => SymbolicValue.Unknown, integers);
        SymbolicValue Read(long offset, Code code, bool dirty = false) { var state = new SymbolicState(); if (dirty) state.Store(data, SymbolicValue.Unknown); state.Stack.Add(FlowMemory.Offset(SymbolicValue.FromStorage(data), offset, false)); memory.Execute(table_root, new Instruction(code == Code.Ldind_I1 ? OpCodes.Ldind_I1 : code == Code.Ldind_U1 ? OpCodes.Ldind_U1 : OpCodes.Ldind_I4), state, Array.Empty<SymbolicValue>()); return state.Stack.Single(); }
        Check("RVA signed byte", Read(0, Code.Ldind_I1).Integer?.Value == -1);
        Check("RVA unsigned byte", Read(0, Code.Ldind_U1).Integer?.Value == 255);
        Check("RVA out of range stays unknown", Read(8, Code.Ldind_I4).IsUnknown);
        Check("RVA negative offset stays unknown", Read(-1, Code.Ldind_I4).IsUnknown);
        Check("modified RVA data is not read as its old file value", Read(4, Code.Ldind_I4, true).IsUnknown);
        var dirty_state = new SymbolicState(); ContextCallEvaluator.InvalidateUnknownCall(dirty_state, new[] { FlowMemory.Offset(SymbolicValue.FromStorage(data), 4, false) });
        Check("RVA address passed to unknown code invalidates the field", dirty_state.Memory.ContainsKey(data));
        var cached_array = new object(); var catalog = new PointerFlowCatalog(); catalog.SetSlot(new FlowAddress("element", cached_array, 0L), SymbolicValue.FromMethod(a)); catalog.SetSlot(new FlowAddress("element", cached_array, 1L), SymbolicValue.FromMethod(b)); catalog.SetSlot(new FlowAddress("length", cached_array, 0), SymbolicValue.FromInteger(integers.Constant(2)));
        var cached_memory = new FlowMemory(catalog, _ => SymbolicValue.Unknown, integers); var cached_state = new SymbolicState(); cached_state.Stack.Add(SymbolicValue.FromObject("System.IntPtr[]", cached_array)); cached_state.Stack.Add(SymbolicValue.Unknown); cached_memory.Execute(table_root, Instruction.Create(OpCodes.Ldelem_I), cached_state, Array.Empty<SymbolicValue>());
        Check("unknown index retains finite initializer-table targets", cached_state.Stack.Single().Methods.SetEquals(new[] { a, b }));
        Check("exact floating arithmetic", FloatingConstant.Binary(Code.Div, 11400.5, 75.5) == 151);
        Check("inexact floating arithmetic is not folded as exact", FloatingConstant.Binary(Code.Add, 0.1, 0.2) is null);
        Check("unordered floating comparison", FloatingConstant.Compare(Code.Blt_Un, double.NaN, 1) == true && FloatingConstant.Compare(Code.Blt, double.NaN, 1) == false);
        var evaluator = new ContextCallEvaluator(new Dictionary<IMethod, MethodDef?>(), CancellationToken.None, _ => SymbolicValue.Unknown);
        Check("boxed floats are not equal merely by numeric value", evaluator.Binary(Code.Ceq, SymbolicValue.Box(SymbolicValue.FromFloating(1)), SymbolicValue.Box(SymbolicValue.FromFloating(1))).Integer?.Value != 1);
        var invocations = new InvocationModel(new[] { module }, reference => reference as MethodDef, (_, _, _) => SymbolicValue.Unknown);
        var convert = new MemberRefUser(module, "ToInt32", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Double), module.CorLibTypes.GetTypeRef("System", "Convert"));
        var conversion_state = new SymbolicState();
        invocations.TryEvaluate(convert, new[] { SymbolicValue.FromFloating(130.5) }, conversion_state, new object(), false, out var converted);
        Check("Convert.ToInt32 uses midpoint-to-even, not truncation", converted.Integer?.Value == 130 && !conversion_state.PathTerminated);
        invocations.TryEvaluate(convert, new[] { SymbolicValue.FromFloating(double.NaN) }, conversion_state, new object(), false, out _);
        Check("invalid floating conversion has no normal continuation", conversion_state.PathTerminated);
        var round = new MemberRefUser(module, "Round", MethodSig.CreateStatic(module.CorLibTypes.Double, module.CorLibTypes.Double), module.CorLibTypes.GetTypeRef("System", "Math"));
        invocations.TryEvaluate(round, new[] { SymbolicValue.FromFloating(131.5) }, new SymbolicState(), new object(), false, out var rounded);
        Check("constant Math.Round is modeled without executing input code", rounded.Floating == 132);
        Console.WriteLine("obfuscation_passed=" + passed);
        return 0;
    }
}
