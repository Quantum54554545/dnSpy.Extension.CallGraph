using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.CallGraph.Core;

static class CorpusInspection {
    static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    public static int Profile(string path, uint? token) {
        using var module = ModuleDefMD.Load(path); var root = token.HasValue ? (MethodDef)module.ResolveToken(token.Value) : module.EntryPoint;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var analysis = new ReachableCallAnalysis(new[] { module }, cancellation.Token);
        var walks = new Dictionary<MethodDef, (long Ms, int Steps, int Count)>();
        analysis.WalkCompleted = (method, ms, steps) => { walks.TryGetValue(method, out var old); walks[method] = (old.Ms + ms, old.Steps + steps, old.Count + 1); };
        var timer = Stopwatch.StartNew(); var slice = analysis.Select(root!, int.MaxValue, 500, CallGraphFilterOptions.None);
        Print(new { elapsed_ms = timer.ElapsedMilliseconds, nodes = slice.Nodes.Count, edges = slice.Links.Count, unresolved = slice.UnresolvedIndirectCallCount });
        foreach (var pair in walks.OrderByDescending(pair => pair.Value.Ms).Take(25)) Print(new { token = pair.Key.MDToken.Raw.ToString("X8"), pair.Key.FullName, pair.Value.Ms, pair.Value.Steps, pair.Value.Count });
        return 0;
    }
    static string Signature(MethodDef method) => method.DeclaringType.FullName + "::" + method.Name + ":" + method.IsStatic + ":" + method.MethodSig.RetType.RemovePinnedAndModifiers().FullName + "(" + string.Join(",", method.MethodSig.Params.Select(type => type.RemovePinnedAndModifiers().FullName)) + ")";
    public static int Match(string baseline_path, string protected_path) {
        using var baseline = ModuleDefMD.Load(baseline_path); using var candidate = ModuleDefMD.Load(protected_path);
        var baseline_methods = baseline.GetTypes().SelectMany(type => type.Methods).GroupBy(Signature).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var protected_methods = candidate.GetTypes().SelectMany(type => type.Methods).GroupBy(Signature).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var pairs = baseline_methods.Where(pair => protected_methods.ContainsKey(pair.Key)).Select(pair => (Original: pair.Value, Protected: protected_methods[pair.Key])).ToArray();
        Print(new { file = Path.GetFileName(protected_path), retained_unique_signatures = pairs.Length, baseline_methods = baseline_methods.Count, meaning = "Metadata identity only; not a proof of implementation equivalence after obfuscation." });
        foreach (var pair in pairs) Print(new { baseline = pair.Original.MDToken.Raw.ToString("X8"), transformed = pair.Protected.MDToken.Raw.ToString("X8"), pair.Original.FullName });
        return 0;
    }
    public static int Check(string directory) {
        var cases = new[] {
            (File: "crackme.exe", Hash: "9D997C3502E5F10AC6BB6C124386B0A5D31D2152ADF503F58FC30F028C63057B", Targets: new uint[] { 0x0600000D }),
            (File: "crackme.armdot.exe", Hash: "66F71223ECA0801D7AA75FAF1519810E86A5DCAD19B56D3BB12A16842AC529B6", Targets: new uint[] { 0x06000194, 0x06000195, 0x06000196, 0x06000197 }),
            (File: "crackme.bestfus.exe", Hash: "526CD51BE23DAB2CAD96EDEDF9404CFEDFD68298D2567A8C723DBA47D4160C72", Targets: new uint[] { 0x06000227, 0x06000ADC, 0x06000AEC }),
            (File: "crackme.HailHydra.exe", Hash: "6777E7CE2B6FABEEBA4BDE2B0B48FC08AE1663F1073EBF042B724372644C1FC8", Targets: new uint[] { 0x06003905, 0x0600396C }),
            (File: "crackme.xerin.exe", Hash: "02381ADC8AC6BEF07808905F61D197082218BA4C24F6530876D09417AA4D809D", Targets: new uint[] { 0x060000FA, 0x060000D7, 0x060002B9, 0x060002BD }),
            (File: "crackme.vmp.exe", Hash: "549BCBF8829DB686169525EB3D0E4F9C0D5BC3D2D039635F00DAFFE812CC3482", Targets: Array.Empty<uint>()),
        };
        foreach (var item in cases) {
            var path = Path.Combine(directory, item.File);
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != item.Hash) throw new InvalidOperationException("Corpus hash mismatch: " + item.File);
            using var module = ModuleDefMD.Load(path); var root = module.EntryPoint ?? throw new InvalidOperationException("Missing entry: " + item.File);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var slice = CallGraphIndex.BuildReachable(root, new[] { module }, int.MaxValue, 500, CallGraphFilterOptions.None, cancellation.Token).Slice;
            var targets = slice.Links.Where(link => link.Source == root).Select(link => link.Target.MDToken.Raw).ToHashSet();
            if (!item.Targets.All(targets.Contains)) throw new InvalidOperationException("Missing expected root relationship: " + item.File);
            if (item.File == "crackme.bestfus.exe" && (!targets.SetEquals(item.Targets) || slice.Links.Where(link => link.Source.MDToken.Raw == 0x06000ADC).Any(link => link.Target.MDToken.Raw != 0x06000AC5))) throw new InvalidOperationException("Dead dispatcher branch leaked into bestfus graph");
            if (item.File == "crackme.xerin.exe" && !slice.Diagnostics.Any(note => note.Kind == GraphDiagnosticKind.UnresolvedDelegate)) throw new InvalidOperationException("Xerin must not claim dynamic proxy targets were resolved");
            if (item.File == "crackme.vmp.exe" && (!slice.IsTruncated || !slice.Diagnostics.Any(note => note.Method == root && note.Kind == GraphDiagnosticKind.UnsupportedInstruction))) throw new InvalidOperationException("Empty VMP entry must be an explicit incomplete boundary");
            Print(new { status = "PASS corpus subset", file = item.File, nodes = slice.Nodes.Count, edges = slice.Links.Count, unresolved_calli = slice.UnresolvedIndirectCallCount, slice.IsTruncated });
            if (item.File == "crackme.armdot.exe") foreach (var table_case in new[] { (Root: 0x06000032U, Initializer: 0x0600009AU), (Root: 0x060000B1U, Initializer: 0x060000E2U) }) {
                var dispatcher = (MethodDef)module.ResolveToken(table_case.Root); var initializer = (MethodDef)module.ResolveToken(table_case.Initializer);
                var addresses = initializer.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Ldftn).Select(instruction => ((IMethod)instruction.Operand).ResolveMethodDef()).ToHashSet();
                var table_graph = CallGraphIndex.BuildReachable(dispatcher, new[] { module }, 32, 500, CallGraphFilterOptions.None, cancellation.Token).Slice;
                var indirect = table_graph.Links.Where(link => link.Source == dispatcher && link.OpCodes.Contains("calli")).ToArray();
                if (indirect.Length == 0 || indirect.Any(link => !addresses.Contains(link.Target))) throw new InvalidOperationException("Initialized pointer table was lost or an unrelated target was invented");
                if (table_case.Root == 0x06000032U && !indirect.Any(link => link.Target.MDToken.Raw == 0x06000038U && link.IlOffsets.Contains(0x016DU))) throw new InvalidOperationException("AntiDump known table target disappeared");
                Print(new { status = "PASS corpus table candidates", root = table_case.Root.ToString("X8"), initialized_addresses = addresses.Count, retained_indirect_targets = indirect.Length, table_graph.UnresolvedIndirectCallCount, meaning = "Candidate retention and exclusion only; not a runtime reachability oracle." });
            }
        }
        Console.WriteLine("PASS corpus_files=6; selected root relationships and incomplete boundaries only, not whole-program equivalence.");
        return 0;
    }
    public static int Dump(string path, uint token) {
        using var module = ModuleDefMD.Load(path);
        var method = (MethodDef)module.ResolveToken(token);
        Print(new { method = method.FullName, method.Attributes, method.ImplAttributes, method.RVA, method.HasBody, method.IsNative, method.IsRuntime, method.IsInternalCall });
        if (method.HasBody) foreach (var instruction in method.Body.Instructions) Console.WriteLine(instruction + (instruction.Operand is IMethod called ? " // token=" + called.MDToken.Raw.ToString("X8") : ""));
        var types = new[] { method.DeclaringType }.Concat(method.HasBody ? method.Body.Instructions.Select(instruction => instruction.Operand).OfType<IField>().Select(field => field.ResolveFieldDef()?.DeclaringType).Where(type => type is not null)! : Array.Empty<TypeDef>()).Distinct();
        foreach (var type in types) {
            Print(new { type = type!.FullName, cctor = type.FindStaticConstructor()?.MDToken.Raw.ToString("X8"), initializer_count = type.FindStaticConstructor()?.Body?.Instructions.Count });
            foreach (var field in type.Fields.Take(25)) Print(new { field = field.FullName, field.Attributes, rva = field.RVA, data_length = field.InitialValue?.Length, data = field.InitialValue is null ? null : Convert.ToHexString(field.InitialValue.Take(64).ToArray()) });
        }
        return 0;
    }
    public static int Inventory(string directory) {
        foreach (var path in Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path) is ".exe" or ".dll").OrderBy(Path.GetFileName)) {
            try {
                using var module = ModuleDefMD.Load(path);
                var methods = module.GetTypes().SelectMany(type => type.Methods).ToArray();
                var bodies = methods.Where(method => method.HasBody).ToArray();
                var operations = bodies.SelectMany(method => method.Body.Instructions).GroupBy(instruction => instruction.OpCode.Name).OrderByDescending(group => group.Count()).ToDictionary(group => group.Key, group => group.Count());
                Print(new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), assembly = module.Assembly?.FullName, module.Machine, module.IsILOnly, module.Is32BitRequired, methods = methods.Length, bodies = bodies.Length, instructions = bodies.Sum(method => method.Body.Instructions.Count), max_body = bodies.Length == 0 ? 0 : bodies.Max(method => method.Body.Instructions.Count), entry = module.EntryPoint?.MDToken.Raw.ToString("X8"), imports = methods.Count(method => method.IsPinvokeImpl), operations });
                if (methods.Length < 150) foreach (var method in methods) Print(new { file = Path.GetFileName(path), token = method.MDToken.Raw.ToString("X8"), name = method.FullName, body = method.HasBody ? method.Body.Instructions.Count : 0 });
            }
            catch (Exception error) { Print(new { file = Path.GetFileName(path), error = error.GetType().Name, detail = error.Message }); }
        }
        return 0;
    }
    public static int Probe(string path, uint? token) {
        using var module = ModuleDefMD.Load(path);
        var root = token.HasValue ? module.ResolveToken(token.Value) as MethodDef : module.EntryPoint;
        if (root is null) throw new ArgumentException("No managed method root.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var timer = Stopwatch.StartNew();
        var slice = CallGraphIndex.BuildReachable(root, new[] { module }, int.MaxValue, 500, CallGraphFilterOptions.None, cancellation.Token).Slice;
        Print(new { file = Path.GetFileName(path), root = root.MDToken.Raw.ToString("X8"), elapsed_ms = timer.ElapsedMilliseconds, nodes = slice.Nodes.Count, edges = slice.Links.Count, unresolved = slice.UnresolvedIndirectCallCount, slice.IsTruncated, diagnostics = slice.Diagnostics.GroupBy(note => note.Kind.ToString()).ToDictionary(group => group.Key, group => group.Count()) });
        foreach (var link in slice.Links.Where(link => link.Source == root)) Print(new { root_target = link.Target.MDToken.Raw.ToString("X8"), link.Target.FullName, link.CallSiteCount, link.IsPotential, offsets = link.IlOffsets.Select(offset => offset.ToString("X4")), operations = link.OpCodes });
        foreach (var note in slice.Diagnostics.Where(note => note.Kind != GraphDiagnosticKind.AddressReference).Take(50)) Print(new { method = note.Method.MDToken.Raw.ToString("X8"), offset = note.Offset.ToString("X4"), kind = note.Kind.ToString(), note.Detail });
        int missing = 0;
        foreach (var node in slice.Nodes.Where(node => node.Method.Module == module && node.Method.HasBody)) {
            var known = slice.Links.Where(link => link.Source == node.Method).SelectMany(link => link.IlOffsets).ToHashSet();
            foreach (var instruction in node.Method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Jmp && instruction.Operand is IMethod)) {
                MethodDef? target;
                try { target = ((IMethod)instruction.Operand).ResolveMethodDef(); } catch { continue; }
                if (target?.Module != module || known.Contains(instruction.Offset)) continue;
                if (missing++ < 60) Print(new { il_only = node.Method.MDToken.Raw.ToString("X8"), offset = instruction.Offset.ToString("X4"), target = target.MDToken.Raw.ToString("X8"), operation = instruction.OpCode.Name });
            }
        }
        Print(new { il_only_sites = missing, note = "Not automatically a missed edge: dead branches, exceptions, dispatch and node bounds require inspection." });
        if (root.HasBody) foreach (var instruction in root.Body.Instructions.Take(100)) Print(new { entry_il = instruction.ToString() });
        return 0;
    }
}
