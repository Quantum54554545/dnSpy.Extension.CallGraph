using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class UiCoverage {
    static Assembly extension=null!;
    static object graph=null!;
    static object index=null!;
    static IDictionary snippets=null!;
    static MethodInfo render_blocks=null!;
    static Type blocks_type=null!,map_type=null!;
    public static int Run(string dnspy_bin, string extension_path){
        AssemblyLoadContext.Default.Resolving+=(_,name)=>{var path=System.IO.Path.Combine(dnspy_bin,name.Name+".dll");return System.IO.File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;};
        extension=AssemblyLoadContext.Default.LoadFromAssemblyPath(System.IO.Path.GetFullPath(extension_path));
        SettingsCoverage.Run(extension);
        using var module=new ModuleDefUser("UiValidation.dll");
        new AssemblyDefUser("UiValidation",new Version(1,0)).Modules.Add(module);
        var fixture=new TypeDefUser("Validation","Methods",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(fixture);
        for(int i=0;i<500;i++){var method=new MethodDefUser("M"+i,MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Static|dnlib.DotNet.MethodAttributes.Public){Body=new CilBody()};method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));fixture.Methods.Add(method);}
        for(int i=0;i<499;i++){fixture.Methods[i].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Call,fixture.Methods[i+1]));fixture.Methods[i].Body.UpdateInstructionOffsets();}
        var index_type=extension.GetType("dnSpy.CallGraph.Core.CallGraphIndex",true)!;
        var filters=extension.GetType("dnSpy.CallGraph.Core.CallGraphFilterOptions",true)!.GetField("None")!.GetValue(null)!;
        var tuple=index_type.GetMethod("BuildReachable")!.Invoke(null,new object[]{fixture.Methods[0],new[]{module},int.MaxValue,500,filters,CancellationToken.None})!;
        graph=tuple.GetType().GetField("Item2")!.GetValue(tuple)!;index=tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var nodes=((IEnumerable)graph.GetType().GetProperty("Nodes")!.GetValue(graph)!).Cast<object>().ToArray();
        var snippet_type=extension.GetType("dnSpy.CallGraph.Core.HighlightedSnippet",true)!;
        var plain=snippet_type.GetMethod("Plain")!;var snippet=plain.Invoke(null,new object[]{"static void Example() { }",Enum.ToObject(plain.GetParameters()[1].ParameterType,0)})!;
        snippets=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(MethodDef),snippet_type))!;
        foreach(var node in nodes)snippets.Add(node.GetType().GetProperty("Method")!.GetValue(node)!,snippet);
        blocks_type=extension.GetType("dnSpy.CallGraph.Controls.CallGraphSurface",true)!;map_type=extension.GetType("dnSpy.CallGraph.Controls.FunctionMapSurface",true)!;
        render_blocks=blocks_type.GetMethods().Single(method=>method.Name=="Render"&&method.GetParameters()[0].ParameterType==graph.GetType()&&method.GetParameters()[1].ParameterType.GenericTypeArguments.Last()==snippet_type);
        foreach(var light in new[]{false,true}){
            var blocks=(FrameworkElement)Activator.CreateInstance(blocks_type)!;Theme(blocks,light);render_blocks.Invoke(blocks,new object[]{graph,snippets});Layout(blocks);PumpFor(70);
            var borders=(IDictionary)blocks_type.GetField("cached_borders",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(blocks)!;
            if(borders.Count==0)throw new Exception("No cards realized");
            var border=(Border)borders.Values.Cast<object>().First();var menu=border.ContextMenu;
            menu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));if(menu.Items.Count<3)throw new Exception("Lazy menu is empty");
            var helper=fixture.Methods[1];var presentations=(IDictionary)blocks_type.GetField("node_presentations",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(blocks)!;
            var helper_menu=(ContextMenu)blocks_type.GetMethod("CreateNodeContextMenu",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(blocks,new[]{presentations[helper]})!;
            if(helper_menu.Items.OfType<MenuItem>().Any(item=>item.Items.Count!=0))throw new Exception("Removed analysis submenu is still present");
            if(helper_menu.Items.Count!=4)throw new Exception("Unexpected Call Blocks context actions");
            var details_type=extension.GetType("dnSpy.CallGraph.Controls.AnalysisDetails",true)!;
            if(details_type.GetMethod("AppendMenu") is not null)throw new Exception("Removed menu builder remains available");
            var summary=(string)details_type.GetMethod("Summary")!.Invoke(null,new object[]{graph,helper})!;
            if(summary.Contains("right-click")||summary.Contains("ПКМ"))throw new Exception("Stale context-menu hint");
            blocks_type.GetMethod("Clear")!.Invoke(blocks,new object?[]{null});
            var map=(FrameworkElement)Activator.CreateInstance(map_type)!;Theme(map,light);map_type.GetMethod("Render")!.Invoke(map,new[]{graph,index});Layout(map);
            var positions=(IDictionary)map_type.GetField("positions",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(map)!;if(positions.Count!=500)throw new Exception("Map omitted nodes");
            var render=map_type.GetMethod("OnRender",BindingFlags.Instance|BindingFlags.NonPublic)!;var drawing=new DrawingVisual();
            var draw_count=map_type.GetField("graph_draw_count",BindingFlags.Instance|BindingFlags.NonPublic)!;int initial_draws=(int)draw_count.GetValue(map)!;
            for(int step=0;step<50;step++){map_type.GetField("zoom",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(map,1+step*0.01);using(var context=drawing.RenderOpen())render.Invoke(map,new object[]{context});}
            int draws=(int)draw_count.GetValue(map)!-initial_draws;if(draws>5)throw new Exception("Small zoom steps redraw the whole map on every frame");Console.WriteLine("PASS retained map drawing zoom50 redraws="+draws);
            var watch=Stopwatch.StartNew();for(int frame=0;frame<50;frame++){map_type.GetField("zoom",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(map,0.8+frame*0.01);map.InvalidateVisual();map.UpdateLayout();Pump();}
            Console.WriteLine($"PASS {(light?"light":"dark")} theme / lazy menu / removed analysis submenus / map500; zoom50_dispatch_ms={watch.ElapsedMilliseconds}");
            map_type.GetMethod("Clear")!.Invoke(map,null);
        }
        var weak=new List<WeakReference>();for(int iteration=0;iteration<12;iteration++){weak.Add(CreateCleared(false));weak.Add(CreateCleared(true));}
        var cfg_type=extension.GetType("dnSpy.CallGraph.Core.ControlFlowGraph",true)!;
        var cfg=cfg_type.GetMethod("Build")!.Invoke(null,new object[]{fixture.Methods[0],500,CancellationToken.None})!;
        var empty_method=new MethodDefUser("EmptyPreview",MethodSig.CreateStatic(module.CorLibTypes.Void),dnlib.DotNet.MethodAttributes.Static|dnlib.DotNet.MethodAttributes.Public){Body=new CilBody()};fixture.Methods.Add(empty_method);
        var empty_preview=cfg_type.GetMethod("MethodPreview")!.Invoke(null,new object[]{empty_method,32})!;
        var empty_text=(string)empty_preview.GetType().GetProperty("Text")!.GetValue(empty_preview)!;
        var expected_empty=(string)extension.GetType("dnSpy.CallGraph.UiText",true)!.GetProperty("NoMethodBody")!.GetValue(null)!;
        if(empty_text!=expected_empty)throw new Exception("Empty IL body looks like an ordinary code preview");
        Console.WriteLine("PASS empty-body preview is an explicit IL boundary");
        var cfg_surface=(FrameworkElement)Activator.CreateInstance(blocks_type)!;Theme(cfg_surface,false);
        blocks_type.GetMethods().Single(method=>method.Name=="Render"&&method.GetParameters()[0].ParameterType==cfg_type).Invoke(cfg_surface,new object[]{cfg,32});Layout(cfg_surface);
        if(((IEnumerable)cfg_type.GetProperty("Blocks")!.GetValue(cfg)!).Cast<object>().Count()==0)throw new Exception("CFG is empty");
        blocks_type.GetMethod("Clear")!.Invoke(cfg_surface,new object?[]{null});Console.WriteLine("PASS CFG rendering and cancellation");
        var canceled=new CancellationTokenSource();canceled.Cancel();bool cfg_canceled=false;try{cfg_type.GetMethod("Build")!.Invoke(null,new object[]{fixture.Methods[0],500,canceled.Token});}catch(TargetInvocationException error)when(error.InnerException is OperationCanceledException){cfg_canceled=true;}if(!cfg_canceled)throw new Exception("CFG cancellation ignored");
        PumpFor(180);for(int cycle=0;cycle<3;cycle++){GC.Collect();GC.WaitForPendingFinalizers();Pump();}
        int retained=weak.Count(reference=>reference.IsAlive);Console.WriteLine($"surface_gc_retained={retained}/{weak.Count}");if(retained!=0)throw new Exception("Graph surfaces retained after Clear");
        return 0;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]static WeakReference CreateCleared(bool map){var type=map?map_type:blocks_type;var element=(FrameworkElement)Activator.CreateInstance(type)!;if(map)type.GetMethod("Render")!.Invoke(element,new[]{graph,index});else render_blocks.Invoke(element,new object[]{graph,snippets});Layout(element);if(map)type.GetMethod("Clear")!.Invoke(element,null);else type.GetMethod("Clear")!.Invoke(element,new object?[]{null});return new WeakReference(element);}
    static void Theme(FrameworkElement element,bool light){element.Resources["SystemColorsWindow"]=light?Brushes.White:Brushes.Black;element.Resources["SystemColorsWindowText"]=light?Brushes.Black:Brushes.White;element.Resources["HyperlinkNormal"]=Brushes.DodgerBlue;element.Resources["CommonControlsTextBoxBorder"]=Brushes.Gray;element.Resources["EnvironmentBackground"]=light?Brushes.WhiteSmoke:Brushes.DimGray;}
    static void Layout(FrameworkElement element){element.Measure(new Size(1400,900));element.Arrange(new Rect(0,0,1400,900));element.UpdateLayout();Pump();}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    static void PumpFor(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
}
