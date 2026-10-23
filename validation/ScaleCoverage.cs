using System.Diagnostics;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class ScaleCoverage {
    sealed class ProgressProbe : IProgress<CallGraphSlice> {
        readonly Stopwatch watch;
        public long First=-1;
        public ProgressProbe(Stopwatch watch)=>this.watch=watch;
        public void Report(CallGraphSlice slice){First=watch.ElapsedMilliseconds;Console.WriteLine($"progress_ms={First} nodes={slice.Nodes.Count}");}
    }
    public static int Run(){
        using var module=new ModuleDefUser("Scale.dll");new AssemblyDefUser("Scale",new Version(1,0)).Modules.Add(module);
        var type=new TypeDefUser("Scale","Methods",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
        for(int index=0;index<100000;index++){var method=new MethodDefUser("M"+index,MethodSig.CreateStatic(module.CorLibTypes.Void),MethodAttributes.Static|MethodAttributes.Public){Body=new CilBody()};method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));type.Methods.Add(method);}
        for(int index=0;index<1000;index++){type.Methods[index].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Call,type.Methods[index+1]));type.Methods[index].Body.UpdateInstructionOffsets();}
        var watch=Stopwatch.StartNew();var probe=new ProgressProbe(watch);
        var graph=CallGraphIndex.BuildReachableProgressive(type.Methods[0],new[]{module},int.MaxValue,500,CallGraphFilterOptions.None,probe).Slice;
        Console.WriteLine($"scale_methods=100000 nodes={graph.Nodes.Count} edges={graph.Links.Count} elapsed_ms={watch.ElapsedMilliseconds} truncated={graph.IsTruncated}");
        if(graph.Nodes.Count!=500||graph.Links.Count!=499||!graph.IsTruncated||probe.First<0)throw new Exception("Scale/limit/progress regression");
        watch.Restart();var index_graph=CallGraphIndex.Build(module);Console.WriteLine($"index100k_ms={watch.ElapsedMilliseconds} links={index_graph.LinkCount} skipped={index_graph.SkippedMethodCount}");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{CallGraphIndex.BuildReachable(type.Methods[0],new[]{module},int.MaxValue,500,CallGraphFilterOptions.None,cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){Console.WriteLine("PASS cancellation");}
        using var during_index = new CancellationTokenSource(); during_index.CancelAfter(10);
        var cancel_watch = Stopwatch.StartNew();
        try { CallGraphIndex.Build(module, during_index.Token); throw new Exception("In-flight index cancellation ignored"); }
        catch (OperationCanceledException) { if (cancel_watch.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Index cancellation exceeded five seconds"); Console.WriteLine("PASS in-flight index cancellation"); }
        return 0;
    }
}
