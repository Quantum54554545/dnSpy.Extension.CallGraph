using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class AuditFixtures {
    public static bool A()=>true;
    public static bool B()=>false;
    public static bool C()=>false;
    public static bool D()=>false;
    public static bool E()=>false;
    public static bool F()=>false;
    public static bool G()=>false;
    public static bool H()=>false;
    public static List<(string,Func<bool>)> Make()=>new(){("a",A),("b",B),("c",C),("d",D),("e",E),("f",F),("g",G),("h",H)};
    public static bool Index(){var checks=new List<(string,Func<bool>)>{("a",A),("b",B)};return checks[0].Item2();}
    public static bool Foreach(){var checks=Make();bool value=false;foreach(var check in checks)value|=check.Item2();return value;}
    public static bool For(){var checks=Make();bool value=false;for(int i=0;i<checks.Count;i++)value|=checks[i].Item2();return value;}
    public static List<(string,Func<bool>)> StoreOnly()=>new(){("a",A),("b",B)};
    public static bool Replaced(){var checks=new List<Func<bool>>{A};checks[0]=B;return checks[0]();}
    public static bool Cleared(){var checks=new List<Func<bool>>{A};checks.Clear();checks.Add(B);return checks[0]();}
    public static bool Removed(){var checks=new List<Func<bool>>{A,B};checks.RemoveAt(0);return checks[0]();}
    public static bool TupleCopy(){var first=("a",(Func<bool>)A);var copy=first;copy.Item2=B;return first.Item2();}
    public static bool TupleRef(){var first=("a",(Func<bool>)A);ref var callback=ref first.Item2;callback=B;return first.Item2();}
    public static bool ArrayCopy(){var checks=new List<Func<bool>>{A,B};return checks.ToArray()[1]();}
    public static bool TupleClass(){var pair=Tuple.Create("a",(Func<bool>)A);return pair.Item2();}
    public static bool Pair(){var pair=new KeyValuePair<string,Func<bool>>("a",A);return pair.Value();}
    public static unsafe bool UnknownCalli(delegate* managed<int> function){int unused=function();return A();}
    public static unsafe bool PointerOnly(){nint address=(nint)(delegate* managed<bool>)&A;return address!=0;}
    static void Noop()=>GC.KeepAlive(null);
    public static bool Catch(){Func<bool> cb=A;try{cb=B;throw new InvalidOperationException();}catch{return cb();}}
    public static bool Finally(){Func<bool> cb=A;try{Noop();}finally{cb=B;}return cb();}
    public static bool NestedFinally(){Func<bool> cb=A;try{try{Noop();}finally{cb=B;}}finally{cb=C;}return cb();}
    static void Throws()=>throw new InvalidOperationException();
    public static bool AfterThrow(){Throws();return A();}
    static Func<bool>? stored;
    static void SetAndThrow(){stored=B;throw new InvalidOperationException();}
    public static bool ThrowWithEffects(){stored=A;try{SetAndThrow();}catch{return stored();}return false;}
    static unsafe nint PointerA()=>(nint)(delegate* managed<bool>)&A;
    static unsafe nint PointerB()=>(nint)(delegate* managed<bool>)&B;
    public static unsafe bool MulticastReturn(){Func<nint> first=PointerA;Func<nint> second=PointerB;return((delegate* managed<bool>)(first+second)())();}
    static void SetB()=>stored=B;
    public static bool VoidDelegateEffects(){stored=A;Action action=SetB;action();return stored();}
    public static bool ArrayForeach(){Func<bool>[] checks={A,B,C,D,E,F,G,H};bool result=false;foreach(var check in checks)result|=check();return result;}
    public static bool AddRange(){var checks=new List<Func<bool>>{A};checks.AddRange(new Func<bool>[]{B,C});return checks[2]();}
    public static bool ReadOnlyOperation(){var checks=new List<Func<bool>>{A};GC.KeepAlive(checks.Contains(B));return checks[0]();}
    static void Shuffle<T>(List<T> values){var random=new Random();for(int i=values.Count-1;i>0;i--){int k=random.Next(i+1);T value=values[k];values[k]=values[i];values[i]=value;}}
    public static bool ShuffledChecks(){var checks=Make();Shuffle(checks);bool result=false;foreach(var check in checks)result|=check.Item2();return result;}
    public static bool ShuffledCopyChecks(){var checks=Make();Shuffle(checks);var copy=new List<(string,Func<bool>)>(checks);bool result=false;foreach(var check in copy)result|=check.Item2();return result;}
    public static bool Reversed(){var checks=new List<Func<bool>>{A,B};checks.Reverse();return checks[0]();}
    public static void ThreadCallback(){var thread=new Thread(()=>B());thread.Start();}
    public static void ThreadCallbackWithOptions(){var thread=new Thread(()=>B()){IsBackground=true};thread.Start();}
}

static class AuditCoverage {
    public static int Run(){
        using var module=ModuleDefMD.Load(Assembly.GetExecutingAssembly().Location);
        var type=module.GetTypes().Single(type=>type.Name==nameof(AuditFixtures));
        int failed=0,passed=0;
        void Check(string name,string[] expected,string[]? absent=null,bool transitive=false){
            var method=type.Methods.Single(method=>method.Name==name);
            var slice=CallGraphIndex.BuildReachable(method,new[]{module},32,500,CallGraphFilterOptions.None).Slice;
            var targets=slice.Links.Where(link=>transitive||link.Source==method).Select(link=>link.Target.Name.ToString()).ToHashSet();
            if(expected.All(targets.Contains)&&(absent??Array.Empty<string>()).All(target=>!targets.Contains(target))){passed++;Console.WriteLine("PASS audit "+name);}
            else {failed++;Console.WriteLine($"FAIL audit {name} expected={string.Join(',',expected)} absent={string.Join(',',absent??Array.Empty<string>())} targets={string.Join(',',targets)}");foreach(var note in slice.Diagnostics.Where(note=>note.Kind!=GraphDiagnosticKind.AddressReference))Console.WriteLine("NOTE "+note);foreach(var instruction in method.Body.Instructions)Console.WriteLine(instruction);}
        }
        var all=new[]{"A","B","C","D","E","F","G","H"};
        Check("Index",new[]{"A"},new[]{"B"});Check("Foreach",all);Check("For",all);
        Check("StoreOnly",Array.Empty<string>(),all,true);Check("Make",Array.Empty<string>(),all,true);
        Check("Replaced",new[]{"B"},new[]{"A"});Check("Cleared",new[]{"B"},new[]{"A"});Check("Removed",new[]{"B"},new[]{"A"});
        Check("TupleCopy",new[]{"A"},new[]{"B"});Check("TupleRef",new[]{"B"},new[]{"A"});
        Check("ArrayCopy",new[]{"B"},new[]{"A"});Check("TupleClass",new[]{"A"});Check("Pair",new[]{"A"});
        Check("UnknownCalli",new[]{"A"});Check("PointerOnly",Array.Empty<string>(),new[]{"A"});
        Check("Catch",new[]{"B"});Check("Finally",new[]{"B"},new[]{"A"});Check("NestedFinally",new[]{"C"},new[]{"A","B"});
        Check("AfterThrow",Array.Empty<string>(),new[]{"A"});Check("ThrowWithEffects",new[]{"B"});
        Check("MulticastReturn",new[]{"B"},new[]{"A"});Check("VoidDelegateEffects",new[]{"B"},new[]{"A"});
        Check("ArrayForeach",all);Check("AddRange",new[]{"C"},new[]{"A","B"});Check("ReadOnlyOperation",new[]{"A"},new[]{"B"});
        Check("ShuffledChecks",all);
        Check("ShuffledCopyChecks",all);Check("Reversed",new[]{"B"},new[]{"A"});
        Check("ThreadCallback",new[]{"B"},transitive:true);
        Check("ThreadCallbackWithOptions",new[]{"B"},transitive:true);
        Console.WriteLine($"audit_passed={passed} failed={failed}");return failed==0?0:1;
    }
}
