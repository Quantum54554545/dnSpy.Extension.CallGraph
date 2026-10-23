using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class FilterCoverage {
    public static int Run() {
        using var app = new ModuleDefUser("App.exe"); using var library = new ModuleDefUser("Other.dll"); using var second = new ModuleDefUser("App.netmodule");
        var assembly = new AssemblyDefUser("App", new Version(1, 0)); assembly.Modules.Add(app); assembly.Modules.Add(second);
        new AssemblyDefUser("Other", new Version(1, 0)).Modules.Add(library);
        MethodDef Method(ModuleDef module, string name) {
            var type = module.GetTypes().FirstOrDefault(type => type.Name == "Methods");
            if (type is null) { type = new TypeDefUser("SameNamespace", "Methods", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type); }
            var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Void), MethodAttributes.Static | MethodAttributes.Public) { Body = new CilBody() }; type.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
        }
        var root = Method(app, "Root"); var local = Method(app, "Local"); var external = Method(library, "External"); var sibling = Method(second, "Sibling");
        external.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, local));
        root.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, external)); root.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Call, sibling)); root.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Call, local));
        foreach (var method in new[] { root, local, external, sibling }) method.Body.UpdateInstructionOffsets();
        var filter = new CallGraphFilterOptions(only_current_assembly: true); int passed = 0;
        void Check(string name, bool success) { if (!success) throw new Exception("Filter regression: " + name); passed++; Console.WriteLine("PASS filter " + name); }
        Check("default keeps external methods", CallGraphFilterOptions.None.Includes(root, external));
        Check("assembly identity not namespace determines visibility", !filter.Includes(root, external));
        Check("other modules of current assembly remain visible", filter.Includes(root, sibling));
        var modules = new[] { app, library, second };
        var forward = CallGraphIndex.BuildReachable(root, modules, 8, 40, filter).Slice;
        Check("forward excludes external nodes and preserves local calls", forward.Nodes.All(node => node.Method.Module.Assembly == assembly) && forward.Links.Any(link => link.Source == root && link.Target == local));
        var index = CallGraphIndex.Combine(modules.Select(module => CallGraphIndex.Build(module)).ToArray());
        foreach (var direction in new[] { CallGraphDirection.Callers, CallGraphDirection.Both }) {
            var graph = index.Select(local, direction, 8, 40, filter);
            Check(direction + " excludes external nodes", graph.Nodes.All(node => node.Method.Module.Assembly == assembly) && !graph.Links.Any(link => link.Source == external || link.Target == external));
        }
        using var unowned = new ModuleDefUser("Unowned"); using var other_unowned = new ModuleDefUser("OtherUnowned");
        Check("modules without assembly do not collapse into one identity", !filter.Includes(Method(unowned, "One"), Method(other_unowned, "Two")));
        var hidden_only = Method(app, "HiddenOnly"); hidden_only.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, external)); hidden_only.Body.UpdateInstructionOffsets();
        var hidden_graph = CallGraphIndex.BuildReachable(hidden_only, modules, 8, 40, filter).Slice;
        Check("hidden library is not bypassed by an invented local edge", hidden_graph.Links.Count == 0);
        Console.WriteLine("filters_passed=" + passed); return 0;
    }
}
