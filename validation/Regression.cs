using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;
using System.Reflection;
using System.Runtime.CompilerServices;

static unsafe class Fixture {
  static int state;
  static readonly nint first = (nint)(delegate* managed<int,int>)&TargetA;
  static readonly nint second = (nint)(delegate* managed<int,int>)&TargetB;
  static nint mutable = (nint)(delegate* managed<int,int>)&TargetA;
  static readonly nint[] table_a = {(nint)(delegate* managed<int,int>)&TargetA};
  static readonly nint[] table_b = {(nint)(delegate* managed<int,int>)&TargetB};
  static int TargetA(int value) => value+1;
  static int TargetB(int value) => value-1;
  static int Opaque() => Interlocked.CompareExchange(ref state,0,0);
  [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
  static int Mix(int a,int b) {
    uint key=(uint)Opaque(); int shift=(int)(key & 31); uint x=(uint)a ^ key;
    x=(x<<shift)|(x>>((32-shift)&31));
    x=(x>>shift)|(x<<((32-shift)&31));
    return (int)(x^key^(uint)b);
  }
  static nint Select(int value) {
    if((uint)value>=2) return 0;
    switch(value){case 0:return(nint)(delegate* managed<int,int>)&TargetA;case 1:return(nint)(delegate* managed<int,int>)&TargetB;default:return 0;}
  }
  static nint Pass(nint value)=>value;
  public static int CallerA(int value)=>((delegate* managed<int,int>)Pass(Select(Mix(0x1234,0x1234))))(value);
  public static int CallerB(int value)=>((delegate* managed<int,int>)Select(Mix(0x1234,0x1235)))(value);
  public static int FieldA(int value)=>((delegate* managed<int,int>)first)(value);
  public static int FieldB(int value)=>((delegate* managed<int,int>)second)(value);
  public static int ArrayA(int value)=>((delegate* managed<int,int>)table_a[0])(value);
  public static int ArrayB(int value)=>((delegate* managed<int,int>)table_b[0])(value);
  public static int Mutation(int value) {
    nint pointer=(nint)(delegate* managed<int,int>)&TargetA;
    pointer^=0x1234; pointer^=0x1234;
    return ((delegate* managed<int,int>)pointer)(value);
  }
  public static nint ReferenceOnly()=>(nint)(delegate* managed<int,int>)&TargetA;
  public static int Unknown(nint pointer,int value) {
    nint unused=(nint)(delegate* managed<int,int>)&TargetB;
    GC.KeepAlive(unused);
    return ((delegate* managed<int,int>)pointer)(value);
  }
  public static int UnknownDispatch(int selector,int value)=>((delegate* managed<int,int>)Select(selector))(value);
  public static int InvalidPointerArithmetic(int value) {
    nint pointer=(nint)(delegate* managed<int,int>)&TargetA;
    return ((delegate* managed<int,int>)(pointer ^ 0x1234))(value);
  }
  delegate int Callback(int value);
  public static int DelegateA(int value) { Callback callback=TargetA; return callback(value); }
  sealed class Construction {
    public Construction(int value) { if(value==0)TargetA(value);else TargetB(value); }
  }
  public static object ConstructA()=>new Construction(0);
  static nint BoxSelector(object value) => value is null ? (nint)(delegate* managed<int,int>)&TargetB : (nint)(delegate* managed<int,int>)&TargetA;
  public static int BoxedZero(int value)=>((delegate* managed<int,int>)BoxSelector((object)0))(value);
  public static int UnknownField(nint pointer,int value){mutable=pointer;return((delegate* managed<int,int>)mutable)(value);}
}

static class Regression {
  public static int Run() {
    using var module=ModuleDefMD.Load(Assembly.GetExecutingAssembly().Location);
    foreach(var type in module.Types.Where(type=>type.Name!=nameof(Fixture) && !type.IsGlobalModuleType).ToArray()) module.Types.Remove(type);
    var index=CallGraphIndex.Build(module);
    var methods=module.GetTypes().Single(type=>type.Name==nameof(Fixture)).Methods.ToDictionary(method=>method.Name.ToString());
    void Check(string name,string? target,int unresolved=0) {
      var source=methods[name];
      var found=index.Outgoing(source).Where(link=>link.Target==methods["TargetA"] || link.Target==methods["TargetB"]).ToArray();
      if(target is null ? found.Length!=0 : found.Length!=1 || found[0].Target!=methods[target]) throw new Exception($"Wrong edges: {name}: {string.Join(",",found.Select(link=>link.Target.Name))}");
      if(index.UnresolvedCalliSites(source).Count!=unresolved) throw new Exception($"Wrong unresolved count: {name}");
      Console.WriteLine($"PASS {name} -> {target??"unresolved/no invocation"}");
    }
    Check("CallerA","TargetA"); Check("CallerB","TargetB");
    Check("FieldA","TargetA"); Check("FieldB","TargetB"); Check("ArrayA","TargetA"); Check("ArrayB","TargetB");
    Check("Mutation","TargetA"); Check("ReferenceOnly",null); Check("Unknown",null,1); Check("InvalidPointerArithmetic",null,1);
    var ambiguous=index.Outgoing(methods["UnknownDispatch"]).Where(link=>link.Target==methods["TargetA"]||link.Target==methods["TargetB"]).ToArray();
    if(ambiguous.Length!=2 || ambiguous.Any(link=>!link.IsPotential))throw new Exception("Unknown selector must preserve possible targets without calling them exact");
    Console.WriteLine("PASS unknown selector preserves two possible, not exact, targets");
    foreach(var name in new[]{"CallerA","CallerB","FieldA","FieldB","ArrayA","ArrayB","Mutation","DelegateA","BoxedZero"}) {
      var slice=CallGraphIndex.BuildReachable(methods[name],new[]{module},16,100,CallGraphFilterOptions.None).Slice;
      string expected=name.EndsWith("B",StringComparison.Ordinal)?"TargetB":"TargetA";
      var calls=slice.Links.Where(link=>link.Source==methods[name] && (link.Target==methods["TargetA"] || link.Target==methods["TargetB"])).ToArray();
      if(calls.Length!=1 || calls[0].Target!=methods[expected]) { foreach(var ins in methods[name].Body.Instructions)Console.WriteLine(ins);foreach(var edge in slice.Links)Console.WriteLine($"FOUND {edge.Source.FullName} -> {edge.Target.FullName} {string.Join(",",edge.OpCodes)}");throw new Exception($"Wrong contextual calls: {name}"); }
      if(name=="CallerA" || name=="CallerB") {
        var other=methods[expected=="TargetA"?"TargetB":"TargetA"];
        if(slice.Nodes.Any(node=>node.Method==other))throw new Exception("Unselected switch case leaked into graph");
      }
      Console.WriteLine($"PASS contextual {name} only -> {expected}");
    }
    Console.WriteLine($"regression_skipped={index.SkippedMethodCount}");
    if(index.SkippedMethodCount!=0){foreach(var note in (IReadOnlyList<GraphDiagnostic>)typeof(CallGraphIndex).GetField("diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(index)!)if(note.Kind==GraphDiagnosticKind.InvalidIl)Console.WriteLine($"INDEX_DIAGNOSTIC {note.Method.FullName} {note.Detail}");throw new Exception("Skipped regression methods");}
    CrossModule();
    var constructor_slice=CallGraphIndex.BuildReachable(methods["ConstructA"],new[]{module},8,40,CallGraphFilterOptions.None).Slice;
    if(!constructor_slice.Nodes.Any(node=>node.Method==methods["TargetA"]) || constructor_slice.Nodes.Any(node=>node.Method==methods["TargetB"]))throw new Exception("Constructor this/argument mapping");
    Console.WriteLine("PASS constructor argument position preserves the selected branch");
    var unknown_field=CallGraphIndex.BuildReachable(methods["UnknownField"],new[]{module},8,40,CallGraphFilterOptions.None).Slice;
    if(unknown_field.Links.Any(link=>link.Source==methods["UnknownField"] && link.Target==methods["TargetA"]) || unknown_field.UnresolvedIndirectCallCount!=1)throw new Exception("Unknown store retained a stale field target");
    Console.WriteLine("PASS unknown field store invalidates stale pointer targets");
    StackEntry();
    return 0;
  }
  static void CrossModule() {
    using var a=new ModuleDefUser("CrossA.dll");using var b=new ModuleDefUser("CrossB.dll");
    new AssemblyDefUser("CrossA",new Version(1,0)).Modules.Add(a);
    new AssemblyDefUser("CrossB",new Version(1,0)).Modules.Add(b);
    var ta=new TypeDefUser("Cross","Caller",a.CorLibTypes.Object.TypeDefOrRef);a.Types.Add(ta);
    var tb=new TypeDefUser("Cross","Library",b.CorLibTypes.Object.TypeDefOrRef);b.Types.Add(tb);
    var target=new MethodDefUser("Target",MethodSig.CreateStatic(b.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static){Body=new CilBody()};tb.Methods.Add(target);target.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    var helper=new MethodDefUser("Pointer",MethodSig.CreateStatic(b.CorLibTypes.IntPtr),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static){Body=new CilBody()};tb.Methods.Add(helper);helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn,target));helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    var root=new MethodDefUser("Root",MethodSig.CreateStatic(a.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static){Body=new CilBody()};ta.Methods.Add(root);
    var reference=new MemberRefUser(a,"Pointer",MethodSig.CreateStatic(a.CorLibTypes.IntPtr),new TypeRefUser(a,"Cross","Library",new AssemblyRefUser(b.Assembly)));
    root.Body.Instructions.Add(Instruction.Create(OpCodes.Call,reference));root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli,MethodSig.CreateStatic(a.CorLibTypes.Void)));root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    var slice=CallGraphIndex.BuildReachable(root,new ModuleDef[]{a,b},8,30,CallGraphFilterOptions.None).Slice;
    if(!slice.Links.Any(link=>link.Source==root && link.Target==helper) || !slice.Links.Any(link=>link.Source==root && link.Target==target && link.OpCodes.Contains("calli")))throw new Exception("Cross-module member reference / calli failed");
    Console.WriteLine("PASS contextual cross-module MemberRef + pointer helper + calli");
  }
  static void StackEntry() {
    using var module=new ModuleDefUser("StackEntry.dll");new AssemblyDefUser("StackEntry",new Version(1,0)).Modules.Add(module);
    var type=new TypeDefUser("Test","Branches",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
    var target=new MethodDefUser("Target",MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static){Body=new CilBody()};type.Methods.Add(target);target.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    var root=new MethodDefUser("Root",MethodSig.CreateStatic(module.CorLibTypes.Void,module.CorLibTypes.Int32,module.CorLibTypes.Int32,module.CorLibTypes.Int32),dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static){Body=new CilBody()};type.Methods.Add(root);
    var constants=Instruction.CreateLdcI4(1);var join=Instruction.Create(OpCodes.Xor);var call=Instruction.Create(OpCodes.Call,target);
    foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Brfalse,constants),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Br,join),constants,Instruction.CreateLdcI4(1),join,Instruction.Create(OpCodes.Brtrue,call),Instruction.Create(OpCodes.Ret),call,Instruction.Create(OpCodes.Ret)})root.Body.Instructions.Add(instruction);
    root.Body.UpdateInstructionOffsets();
    if(!CallGraphIndex.Build(module).Outgoing(root).Any(link=>link.Target==target))throw new Exception("Constant folding crossed a branch target");
    if(!CallGraphIndex.BuildReachable(root,new[]{module},8,20,CallGraphFilterOptions.None).Slice.Links.Any(link=>link.Target==target))throw new Exception("Stack merge removed a feasible call");
    Console.WriteLine("PASS branch entry into an expression is not folded across basic blocks");
  }
}
