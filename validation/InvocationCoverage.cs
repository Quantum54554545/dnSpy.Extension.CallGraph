using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.CallGraph.Core;

static class InvocationFixtures {
    public static int A(int value) => value + 1;
    public static int B(int value) => value - 1;
    public delegate int Callback(int value);
    public sealed class Box {
        public nint Pointer;
        public unsafe Box(int which) { Pointer = which == 0 ? (nint)(delegate* managed<int,int>)&A : (nint)(delegate* managed<int,int>)&B; }
    }
    public static unsafe int ThroughConstructor(int value) { var box = new Box(0); return ((delegate* managed<int,int>)box.Pointer)(value); }
    public static unsafe int DistinctObjects(int value) { var first = new Box(0); var second = new Box(1); return ((delegate* managed<int,int>)first.Pointer)(value); }
    static unsafe void Out(out nint pointer) => pointer = (nint)(delegate* managed<int,int>)&A;
    static nint Ref(ref nint pointer) => pointer;
    public static unsafe int ThroughOut(int value) { Out(out var pointer); return ((delegate* managed<int,int>)pointer)(value); }
    public static unsafe int ThroughRef(int value) { nint pointer = (nint)(delegate* managed<int,int>)&A; return ((delegate* managed<int,int>)Ref(ref pointer))(value); }
    public static unsafe int ReplacePointer(int value) { var box = new Box(0); box.Pointer = (nint)(delegate* managed<int,int>)&B; return ((delegate* managed<int,int>)box.Pointer)(value); }
    static Callback Factory() => A;
    public static int DelegateFactory(int value) => Factory()(value);
    public static int Combined(int value) { Callback first=A; Callback second=B; return (first+second)(value); }
    public static int Removed(int value) { Callback first=A; return (first-first)?.Invoke(value) ?? 0; }
    public static Callback AddressOnly() => A;
    public static object? Reflection() => typeof(InvocationFixtures).GetMethod(nameof(A))!.Invoke(null,new object[]{1});
    public static int CreatedDelegate(int value) => ((Callback)Delegate.CreateDelegate(typeof(Callback),typeof(InvocationFixtures).GetMethod(nameof(A))!))(value);
    public static int MethodInfoDelegate(int value) => typeof(InvocationFixtures).GetMethod(nameof(A))!.CreateDelegate<Callback>()(value);
    public static unsafe int MarshalDelegate(int value) => Marshal.GetDelegateForFunctionPointer<Callback>((nint)(delegate* managed<int,int>)&A)(value);
    public static nint MethodHandle() => typeof(InvocationFixtures).GetMethod(nameof(A))!.MethodHandle.GetFunctionPointer();
    public static unsafe int ThroughMethodHandle(int value) => ((delegate* managed<int,int>)MethodHandle())(value);
    public static Task<int> Scheduled() => Task.Run(()=>A(1));
    public static async Task<int> Async() { await Task.Yield(); return A(1); }
    public static IEnumerable<int> Iterator() { yield return A(1); }
    public static int ConsumeIterator() { foreach(var value in Iterator()) return value; return 0; }
    public interface IWorker { int Invoke(int value); }
    public sealed class Worker:IWorker { public int Invoke(int value)=>A(value); }
    public static int Interface(int value) { IWorker worker=new Worker();return worker.Invoke(value); }
    public class Base { public virtual int Run(int value)=>B(value); }
    public sealed class Derived:Base { public override int Run(int value)=>A(value); }
    public static int Virtual(int value) { Base worker=new Derived();return worker.Run(value); }
    public static int Dynamic(int value) { dynamic worker=new Worker();return worker.Invoke(value); }
    static event Callback? Event;
    public static void SubscribeOnly() { Event+=A; }
    public static int EventInvoke(int value) { Event+=A;return Event?.Invoke(value)??0; }
    public static int TailCandidate(int value)=>A(value);
    public static T Generic<T>(T value)=>value;
    public static unsafe int GenericPointer(int value)=>((delegate* managed<int,int>)&Generic<int>)(value);
    public static unsafe int ParameterField(Box box,int value){box.Pointer=(nint)(delegate* managed<int,int>)&A;return((delegate* managed<int,int>)box.Pointer)(value);}
    public static unsafe int GenericHandle(int value)=>((delegate* managed<int,int>)typeof(InvocationFixtures).GetMethod(nameof(Generic))!.MakeGenericMethod(typeof(int)).MethodHandle.GetFunctionPointer())(value);
    static Box Make()=>new Box(0);
    public static unsafe int FactoryObjects(int value){var first=Make();var second=Make();second.Pointer=(nint)(delegate* managed<int,int>)&B;return((delegate* managed<int,int>)first.Pointer)(value);}
    static nint phase;
    static unsafe int Wrapper(int value)=>((delegate* managed<int,int>)phase)(value);
    public static unsafe int GlobalContexts(){phase=(nint)(delegate* managed<int,int>)&A;var a=Wrapper(1);phase=(nint)(delegate* managed<int,int>)&B;return a+Wrapper(1);}
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    public static uint NativeBoundary()=>GetTickCount();
    public static int RemoveLast(int value){Callback first=A;Callback second=B;return ((first+second)-second)!(value);}
}

static class InvocationCoverage {
    public static int Run() {
        using var module=ModuleDefMD.Load(Assembly.GetExecutingAssembly().Location);
        var type=module.GetTypes().Single(type=>type.Name==nameof(InvocationFixtures));
        var a=type.Methods.Single(method=>method.Name=="A");var b=type.Methods.Single(method=>method.Name=="B");
        int failed=0, passed=0;
        void Check(string name,string[] expected,bool direct=true,string[]? absent=null) {
            var method=type.Methods.Single(method=>method.Name==name);
            var slice=CallGraphIndex.BuildReachable(method,new[]{module},32,400,CallGraphFilterOptions.None).Slice;
            var links=slice.Links.Where(link=>!direct||link.Source==method).ToArray();
            var names=links.Select(link=>link.Target.Name.ToString()).ToHashSet(StringComparer.Ordinal);
            bool success=expected.All(names.Contains)&&(absent??Array.Empty<string>()).All(name=>!names.Contains(name));
            if(success){passed++;Console.WriteLine("PASS "+name);}else {failed++;Console.WriteLine($"FAIL {name} expected={string.Join(",",expected)} got={string.Join(",",names)} unknown={slice.UnresolvedIndirectCallCount}");foreach(var instruction in method.Body.Instructions)Console.WriteLine("  "+instruction);if(name=="ThroughMethodHandle")foreach(var instruction in type.Methods.Single(method=>method.Name=="MethodHandle").Body.Instructions)Console.WriteLine("  HELPER "+instruction);if(name=="EventInvoke")foreach(var instruction in type.Methods.Single(method=>method.Name=="add_Event").Body.Instructions)Console.WriteLine("  ADD "+instruction);}
        }
        Check("ThroughConstructor",new[]{"A"});Check("DistinctObjects",new[]{"A"},absent:new[]{"B"});
        Check("ThroughOut",new[]{"A"});Check("ThroughRef",new[]{"A"});Check("ReplacePointer",new[]{"B"},absent:new[]{"A"});
        Check("DelegateFactory",new[]{"A"});Check("Combined",new[]{"A","B"});Check("Removed",Array.Empty<string>(),absent:new[]{"A"});
        Check("AddressOnly",Array.Empty<string>(),absent:new[]{"A"});
        Check("Reflection",new[]{"A"});Check("CreatedDelegate",new[]{"A"});Check("MethodInfoDelegate",new[]{"A"});Check("MarshalDelegate",new[]{"A"});Check("ThroughMethodHandle",new[]{"A"});
        Check("Scheduled",new[]{"A"},false);Check("Async",new[]{"MoveNext","A"},false);Check("ConsumeIterator",new[]{"A"},false);
        Check("Interface",new[]{"A"},false);Check("Virtual",new[]{"A"},false,new[]{"B"});
        Check("Dynamic",new[]{"A"},false);Check("SubscribeOnly",Array.Empty<string>(),absent:new[]{"A"});Check("EventInvoke",new[]{"A"});Check("GenericPointer",new[]{"Generic"});
        Check("ParameterField",new[]{"A"});Check("GenericHandle",new[]{"Generic"});
        Check("FactoryObjects",new[]{"A"},absent:new[]{"B"});Check("GlobalContexts",new[]{"A","B"},false);
        Check("RemoveLast",new[]{"A"},absent:new[]{"B"});
        var native=CallGraphIndex.BuildReachable(type.Methods.Single(method=>method.Name=="NativeBoundary"),new[]{module},4,20,CallGraphFilterOptions.None).Slice;
        if(!native.Diagnostics.Any(item=>item.Kind==GraphDiagnosticKind.NativeBoundary)){failed++;Console.WriteLine("FAIL native boundary diagnosis");}else{passed++;Console.WriteLine("PASS native boundary diagnosis (not executed)");}
        Console.WriteLine($"coverage_passed={passed} failed={failed}");return failed==0?0:1;
    }
}
