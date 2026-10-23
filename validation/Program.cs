internal static class Program {
    [STAThread]
    static int Main(string[] args) {
        try {
            Console.WriteLine($"VALIDATION runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} process_arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            var suites = new Dictionary<string, Func<int>>(StringComparer.OrdinalIgnoreCase) {
                ["regression"] = Regression.Run,
                ["invocation"] = InvocationCoverage.Run,
                ["pointers"] = InvocationGapDiagnostic.Run,
                ["containers"] = AuditCoverage.Run,
                ["il"] = AuditIlCoverage.Run,
                ["soundness"] = SoundnessCoverage.Run,
                ["differential"] = DifferentialCoverage.Run,
                ["floating"] = FloatingDifferential.Run,
                ["malformed"] = MalformedCoverage.Run,
                ["obfuscation"] = ObfuscationCoverage.Run,
                ["tables"] = TableFlowCoverage.Run,
                ["filters"] = FilterCoverage.Run,
                ["scale"] = ScaleCoverage.Run,
            };
            if (args.Length == 2 && args[0] == "inspect") return SampleInspection.Run(args[1]);
            if (args.Length == 2 && args[0] == "corpus-info") return CorpusInspection.Inventory(args[1]);
            if (args.Length >= 2 && args[0] == "corpus-probe") return CorpusInspection.Probe(args[1], args.Length > 2 ? Convert.ToUInt32(args[2], 16) : null);
            if (args.Length >= 2 && args[0] == "corpus-profile") return CorpusInspection.Profile(args[1], args.Length > 2 ? Convert.ToUInt32(args[2], 16) : null);
            if (args.Length == 3 && args[0] == "corpus-dump") return CorpusInspection.Dump(args[1], Convert.ToUInt32(args[2], 16));
            if (args.Length == 3 && args[0] == "corpus-match") return CorpusInspection.Match(args[1], args[2]);
            if (args.Length == 2 && args[0] == "corpus-check") return CorpusInspection.Check(args[1]);
            if (args.Length == 3 && args[0] == "ui") return UiCoverage.Run(args[1], args[2]);
            if (args.Length == 1 && suites.TryGetValue(args[0], out var selected)) return selected();
            if (args.Length != 0) throw new ArgumentException("Use a suite name, inspect <path>, or no arguments for all suites.");
            foreach (var suite in suites) {
                Console.WriteLine("SUITE " + suite.Key);
                if (suite.Value() != 0) return 1;
            }
            Console.WriteLine("ALL SUITES PASSED");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
