using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using System.Reflection;
using RuntimeEmit = System.Reflection.Emit;

static class DifferentialCoverage {
    public static int Left() => 111;
    public static int Right() => 222;

    public static int Run() {
        using var module = new ModuleDefUser("Generated.dll");
        new AssemblyDefUser("Generated", new Version(1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Generated", "Methods", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
        MethodDef Method(string name) {
            var result = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
            type.Methods.Add(result); return result;
        }
        var left = Method(nameof(Left)); left.Body.Instructions.Add(Instruction.CreateLdcI4(111)); left.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var right = Method(nameof(Right)); right.Body.Instructions.Add(Instruction.CreateLdcI4(222)); right.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var runtime_ops = typeof(RuntimeEmit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(RuntimeEmit.OpCode)).Select(field => (RuntimeEmit.OpCode)field.GetValue(null)!).ToDictionary(op => op.Name!, StringComparer.Ordinal);
        var random = new Random(335);
        var binary = new[] { OpCodes.Add, OpCodes.Sub, OpCodes.Mul, OpCodes.Xor, OpCodes.And, OpCodes.Or, OpCodes.Shl, OpCodes.Shr, OpCodes.Shr_Un, OpCodes.Div, OpCodes.Div_Un, OpCodes.Rem, OpCodes.Rem_Un };
        var comparisons = new[] { OpCodes.Beq, OpCodes.Bne_Un, OpCodes.Blt, OpCodes.Blt_Un, OpCodes.Bge, OpCodes.Bge_Un, OpCodes.Bgt, OpCodes.Bgt_Un, OpCodes.Ble, OpCodes.Ble_Un };
        var constants = new[] { int.MinValue, int.MaxValue, -1, 0, 1, 31, 32, 63, 64, 255, 65535 };
        int passed = 0;
        for (int iteration = 0; iteration < 512; iteration++) {
            bool wide = iteration >= 256;
            var method = Method("Case" + iteration);
            var body = method.Body.Instructions;
            long seed = constants[random.Next(constants.Length)];
            if (wide) seed = unchecked((seed << 32) ^ (uint)random.Next());
            body.Add(wide ? Instruction.Create(OpCodes.Ldc_I8, seed) : Instruction.CreateLdcI4((int)seed));
            for (int term = 0; term < 5; term++) {
                var operation = binary[random.Next(binary.Length)];
                bool shift = operation.Code is Code.Shl or Code.Shr or Code.Shr_Un;
                long value = constants[random.Next(constants.Length)];
                if (operation.Code is Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un) value = random.Next(1, 65);
                body.Add(wide && !shift ? Instruction.Create(OpCodes.Ldc_I8, value) : Instruction.CreateLdcI4((int)value));
                body.Add(Instruction.Create(operation));
                if (!wide && term == 2) body.Add(Instruction.Create(new[] { OpCodes.Conv_I1, OpCodes.Conv_U1, OpCodes.Conv_I2, OpCodes.Conv_U2 }[random.Next(4)]));
            }
            long pivot = constants[random.Next(constants.Length)];
            body.Add(wide ? Instruction.Create(OpCodes.Ldc_I8, pivot) : Instruction.CreateLdcI4((int)pivot));
            bool indirect = iteration % 2 == 0;
            var yes = Instruction.Create(indirect ? OpCodes.Ldftn : OpCodes.Call, left);
            body.Add(Instruction.Create(comparisons[random.Next(comparisons.Length)], yes));
            body.Add(Instruction.Create(indirect ? OpCodes.Ldftn : OpCodes.Call, right));
            if (indirect) body.Add(Instruction.Create(OpCodes.Calli, right.MethodSig));
            body.Add(Instruction.Create(OpCodes.Ret));
            body.Add(yes);
            if (indirect) body.Add(Instruction.Create(OpCodes.Calli, left.MethodSig));
            body.Add(Instruction.Create(OpCodes.Ret));
            method.Body.UpdateInstructionOffsets();

            var runtime = new RuntimeEmit.DynamicMethod(method.Name, typeof(int), Type.EmptyTypes, typeof(DifferentialCoverage), true);
            var emit = runtime.GetILGenerator();
            var labels = body.ToDictionary(instruction => instruction, _ => emit.DefineLabel());
            foreach (var instruction in body) {
                emit.MarkLabel(labels[instruction]);
                var operation = runtime_ops[instruction.OpCode.Name];
                switch (instruction.Operand) {
                    case null: emit.Emit(operation); break;
                    case int value: emit.Emit(operation, value); break;
                    case sbyte value: emit.Emit(operation, value); break;
                    case long value: emit.Emit(operation, value); break;
                    case Instruction target: emit.Emit(operation, labels[target]); break;
                    case MethodDef target: emit.Emit(operation, typeof(DifferentialCoverage).GetMethod(target.Name)!); break;
                    case MethodSig: emit.EmitCalli(operation, CallingConventions.Standard, typeof(int), Type.EmptyTypes, null); break;
                    default: throw new InvalidOperationException("Unexpected generated operand");
                }
            }
            int actual = ((Func<int>)runtime.CreateDelegate(typeof(Func<int>)))();
            var expected = actual == 111 ? left : actual == 222 ? right : throw new InvalidOperationException("Unexpected sentinel result");
            var graph = CallGraphIndex.BuildReachable(method, new[] { module }, 8, 20, CallGraphFilterOptions.None).Slice;
            var targets = graph.Links.Where(link => link.Source == method).Select(link => link.Target).ToArray();
            if (targets.Length != 1 || targets[0] != expected || graph.IsTruncated || graph.UnresolvedIndirectCallCount != 0) {
                Console.WriteLine($"FAIL differential seed=335 case={iteration} expected={expected.Name} actual={string.Join(',', targets.Select(target => target.Name))}");
                foreach (var instruction in body) Console.WriteLine(instruction);
                return 1;
            }
            passed++;
        }
        Console.WriteLine($"PASS differential generated_cases={passed} seed=335 (only generated validation IL was executed)");
        return 0;
    }
}
