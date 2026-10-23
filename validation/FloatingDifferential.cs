using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using RuntimeEmit = System.Reflection.Emit;

static class FloatingDifferential {
    public static int Run() {
        using var module = new ModuleDefUser("FloatingCases.dll"); new AssemblyDefUser("FloatingCases", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Validation", "Methods", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
        MethodDef Method(string name) { var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() }; type.Methods.Add(method); return method; }
        var left = Method(nameof(DifferentialCoverage.Left)); left.Body.Instructions.Add(Instruction.CreateLdcI4(111)); left.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var right = Method(nameof(DifferentialCoverage.Right)); right.Body.Instructions.Add(Instruction.CreateLdcI4(222)); right.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var convert = new MemberRefUser(module, "ToInt32", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Double), module.CorLibTypes.GetTypeRef("System", "Convert"));
        var random = new Random(33513);
        var operations = new[] { OpCodes.Add, OpCodes.Sub, OpCodes.Mul, OpCodes.Div };
        var runtime_operations = new[] { RuntimeEmit.OpCodes.Add, RuntimeEmit.OpCodes.Sub, RuntimeEmit.OpCodes.Mul, RuntimeEmit.OpCodes.Div };
        for (int iteration = 0; iteration < 128; iteration++) {
            double a = random.Next(-4096, 4097) / 2.0;
            double b = iteration % 4 == 3 ? 1 << random.Next(0, 8) : random.Next(-1024, 1025) / 2.0;
            int pivot = random.Next(-4096, 4097);
            string operation_name = iteration % 2 == 0 ? "Round" : "Truncate";
            var transform = new MemberRefUser(module, operation_name, MethodSig.CreateStatic(module.CorLibTypes.Double, module.CorLibTypes.Double), module.CorLibTypes.GetTypeRef("System", "Math"));
            var method = Method("Case" + iteration); var body = method.Body.Instructions;
            var yes = Instruction.Create(OpCodes.Call, left);
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldc_R8, a), Instruction.Create(OpCodes.Ldc_R8, b), Instruction.Create(operations[iteration % 4]), Instruction.Create(OpCodes.Call, transform), Instruction.Create(OpCodes.Call, convert), Instruction.CreateLdcI4(pivot), Instruction.Create(OpCodes.Bgt, yes), Instruction.Create(OpCodes.Call, right), Instruction.Create(OpCodes.Ret), yes, Instruction.Create(OpCodes.Ret) }) body.Add(instruction);
            method.Body.UpdateInstructionOffsets();
            var runtime = new RuntimeEmit.DynamicMethod("Floating" + iteration, typeof(int), Type.EmptyTypes, typeof(FloatingDifferential), true); var emit = runtime.GetILGenerator(); var label = emit.DefineLabel();
            emit.Emit(RuntimeEmit.OpCodes.Ldc_R8, a); emit.Emit(RuntimeEmit.OpCodes.Ldc_R8, b); emit.Emit(runtime_operations[iteration % 4]);
            emit.Emit(RuntimeEmit.OpCodes.Call, typeof(Math).GetMethod(operation_name, new[] { typeof(double) })!);
            emit.Emit(RuntimeEmit.OpCodes.Call, typeof(Convert).GetMethod("ToInt32", new[] { typeof(double) })!);
            emit.Emit(RuntimeEmit.OpCodes.Ldc_I4, pivot); emit.Emit(RuntimeEmit.OpCodes.Bgt, label);
            emit.Emit(RuntimeEmit.OpCodes.Call, typeof(DifferentialCoverage).GetMethod(nameof(DifferentialCoverage.Right))!); emit.Emit(RuntimeEmit.OpCodes.Ret);
            emit.MarkLabel(label); emit.Emit(RuntimeEmit.OpCodes.Call, typeof(DifferentialCoverage).GetMethod(nameof(DifferentialCoverage.Left))!); emit.Emit(RuntimeEmit.OpCodes.Ret);
            int actual = ((Func<int>)runtime.CreateDelegate(typeof(Func<int>)))();
            var expected = actual == 111 ? left : right;
            var graph = CallGraphIndex.BuildReachable(method, new[] { module }, 8, 20, CallGraphFilterOptions.None).Slice;
            var targets = graph.Links.Where(link => link.Source == method && (link.Target == left || link.Target == right)).ToArray();
            if (targets.Length != 1 || targets[0].Target != expected || graph.IsTruncated) throw new Exception($"Floating differential failure: seed=33513 case={iteration} expected={expected.Name}");
        }
        Console.WriteLine("PASS floating generated_cases=128 seed=33513 (exact dyadic arithmetic / Round / Truncate / Convert, own generated IL only)");
        return 0;
    }
}
