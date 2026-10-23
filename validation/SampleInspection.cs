using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using System.Diagnostics;

static class SampleInspection {
    public static int Run(string path) {
        using var module = ModuleDefMD.Load(path);
        var root = module.EntryPoint ?? throw new ArgumentException("The input has no managed entry point.");
        var watch = Stopwatch.StartNew();
        var slice = CallGraphIndex.BuildReachable(root, new[] { module }, int.MaxValue, 2000, CallGraphFilterOptions.None).Slice;
        Console.WriteLine($"Static metadata only; input was not executed.\nanalysis_ms={watch.ElapsedMilliseconds} nodes={slice.Nodes.Count} links={slice.Links.Count} unresolved={slice.UnresolvedIndirectCallCount} truncated={slice.IsTruncated}");
        int absent = 0;
        foreach (var node in slice.Nodes.Where(node => node.Method.Module == module && node.Method.HasBody)) {
            var offsets = slice.Links.Where(link => link.Source == node.Method).SelectMany(link => link.IlOffsets).ToHashSet();
            foreach (var instruction in node.Method.Body.Instructions.Where(item => item.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && item.Operand is IMethod)) {
                MethodDef? target;
                try { target = ((IMethod)instruction.Operand).ResolveMethodDef(); } catch { continue; }
                if (target?.Module != module || offsets.Contains(instruction.Offset)) continue;
                absent++;
                Console.WriteLine($"IL_ONLY {node.Method.MDToken} IL_{instruction.Offset:X4} -> {target.MDToken}");
            }
        }
        Console.WriteLine($"internal_direct_sites_absent_from_visited_graph={absent}");
        Console.WriteLine("IL_ONLY is a diagnostic, not necessarily a missed call: dead branches, known exceptions, dispatch and node bounds can exclude sites.");
        foreach (var note in slice.Diagnostics.Where(note => note.Kind != GraphDiagnosticKind.AddressReference).Take(30)) Console.WriteLine($"{note.Method.MDToken} IL_{note.Offset:X4} {note.Kind}: {note.Detail}");
        return 0;
    }
}
