using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class MalformedCoverage {
    public static int Run() {
        var random = new Random(335);
        for (int iteration = 0; iteration < 256; iteration++) {
            using var module = new ModuleDefUser("Malformed" + iteration);
            new AssemblyDefUser("Malformed", new Version(1, 0)).Modules.Add(module);
            var type = new TypeDefUser("Validation", "Methods", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
            MethodDef Method(string name) { var result = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() }; type.Methods.Add(result); return result; }
            var target = Method("Target"); target.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            var root = Method("Root"); var body = root.Body.Instructions;
            for (int padding = random.Next(10); padding > 0; padding--) body.Add(Instruction.Create(OpCodes.Nop));
            int mutation = iteration % 8;
            switch (mutation) {
                case 0: body.Add(Instruction.Create(OpCodes.Pop)); body.Add(Instruction.Create(OpCodes.Call, target)); body.Add(Instruction.Create(OpCodes.Ret)); break;
                case 1: body.Add(Instruction.Create(OpCodes.Br, Instruction.Create(OpCodes.Nop))); break;
                case 2: body.Add(Instruction.CreateLdcI4(0)); body.Add(new Instruction(OpCodes.Switch)); break;
                case 3: body.Add(Instruction.CreateLdcI4(1)); body.Add(Instruction.Create(OpCodes.Ret)); break;
                case 4: body.Add(Instruction.Create(OpCodes.Call, target)); body.Add(Instruction.Create(OpCodes.Pop)); body.Add(Instruction.Create(OpCodes.Ret)); break;
                case 5: body.Add(Instruction.Create(OpCodes.Call, target)); break;
                case 6: var grow = Instruction.Create(OpCodes.Dup); body.Add(Instruction.CreateLdcI4(1)); body.Add(grow); body.Add(Instruction.Create(OpCodes.Br, grow)); break;
                case 7: body.Add(Instruction.CreateLdcI4(random.Next())); body.Add(new Instruction(OpCodes.Brtrue)); body.Add(Instruction.Create(OpCodes.Call, target)); body.Add(Instruction.Create(OpCodes.Ret)); break;
            }
            root.Body.UpdateInstructionOffsets();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var slice = CallGraphIndex.BuildReachable(root, new[] { module }, int.MaxValue, 500, CallGraphFilterOptions.None, cancellation.Token).Slice;
            var index = CallGraphIndex.Build(module, cancellation.Token);
            if (!slice.IsTruncated || !slice.Diagnostics.Any(note => note.Kind == GraphDiagnosticKind.InvalidIl) || slice.Links.Any(link => link.Source == root) || index.Outgoing(root).Count != 0) throw new Exception($"Malformed IL was accepted as a valid call graph: seed=335 case={iteration} mutation={mutation}");
        }
        Console.WriteLine("PASS malformed generated_cases=256 seed=335 (stack, branches, return, fall-through; not metadata-parser fuzzing)");
        return 0;
    }
}
