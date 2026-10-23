# Reproducible validation

Requirements: Windows, the .NET 10 SDK, and a matching dnSpyEx distribution. No NuGet test framework or inspected sample is required. The suite links the actual analysis sources; it does not maintain a second implementation.

From the repository root in PowerShell:

```powershell
$dnspy_bin = 'X:\path\to\dnSpy\bin'
dotnet build .\validation\Validation.csproj -c Release "-p:DnSpyBinDir=$dnspy_bin"
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll
```

The process returns a nonzero exit code on failure. A successful run ends with `ALL SUITES PASSED`. To repeat under x86, invoke the same AnyCPU validation DLL using an installed x86 .NET 10 Windows Desktop runtime. An x64 run alone does not validate x86 behavior.

## Coverage

| Suite | Checks | Scope |
| --- | ---: | --- |
| `regression` | 24 | Contexts, workspace resolution, fields, arguments, selected pointer helpers and reverse index |
| `invocation` | 29 | Delegates, reflection, virtual/interface dispatch, generics, scheduling, address-only negatives |
| `pointers` | 11 | Pointer flow, signatures, local/instance addresses, tail calls and known virtual receivers |
| `containers` | 30 | Lists, arrays, tuples, copies, shuffle, delegate effects, exceptions and thread callbacks |
| `il` | 36 | Numeric width, ABI, stack/branch validation, limits, cancellation and structural recovery |
| `soundness` | 15 | Stale state after unknown calls, indirect effects, known exceptions, modified-void signatures and invalid reverse-index edges |
| `differential` | 512 | Generated direct/calli branch graphs compared with execution by the CLR, fixed seed 335 |
| `floating` | 128 | Exact dyadic arithmetic and Round/Truncate/Convert compared with generated CLR execution, seed 33513 |
| `malformed` | 256 | Eight invalid-IL templates with seeded padding; rejection and diagnostics in forward/reverse analysis |
| `obfuscation` | 17 | Opaque initializers, empty bodies, RVA reads/addresses, cached pointer tables and floating-point negatives |
| `tables` | 30 | Lazy/materialized pointer tables, finite index partitions, 79 by-ref arguments, candidate aliases, repeated allocations, array bounds, runtime-type descriptors, escaped/overlapping writes, virtual uncertainty and cache invalidation |
| `filters` | 8 | Assembly identity, multiple modules, defaults, forward/Callers/Both filtering, unowned modules and no invented bypass edges |

These total 1,096 cases. The focused `regression` index contains the Fixture type and its nested types, not the entire validation harness/analyzer implementation. `scale` additionally creates 100,000 methods, checks a 500-node/499-link limit, progress notification, reverse indexing and cancellation. It is a synthetic load check, not an interactive dnSpy benchmark.

Run one suite by appending its name. The differential suite executes only its own generated validation IL and two harmless sentinel methods. Other suites inspect generated or validation-module metadata; they do not invoke methods from user-supplied binaries. The malformed suite is deliberately narrow: it is not a metadata-parser fuzzer or a security certification.

## Graph surfaces

Build the extension first, then run:

```powershell
dotnet build .\CallGraph.Extension.csproj -c Release "-p:DnSpyBinDir=$dnspy_bin"
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll ui $dnspy_bin .\bin\Release\CallGraph.Extension.x.dll
```

This WPF smoke check uses a generated 500-method graph and the actual built extension. It checks Call Blocks, Function Map, CFG, light/dark resource dictionaries, context menus, cancellation and collection of 24 cleared surfaces. Snippets are placeholders: this does not measure real C# decompilation, the complete host application, mouse-to-display latency, compositor FPS or every third-party theme. Run the UI check separately from the core suite.

The settings check instantiates the actual settings page using an in-memory implementation of dnSpy's settings interfaces; it verifies default-off, checkbox apply, persisted reload, filter propagation and turning the option off. It does not modify the user's options. Map rendering tests invoke the actual OnRender override over 50 small zoom steps and require no more than five full map redraws; this is a retained-drawing regression, not an FPS measurement.

## Inspect an additional input

```powershell
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll inspect 'X:\samples\input.exe'
```

This command reads metadata only. It reports a forward graph from the managed entry point, limits/unknowns and internal direct-call sites absent from the visited graph. It is a diagnostic, not an automatic correctness oracle: dead branches, known exceptions, dispatch and graph bounds can legitimately exclude a site. It does not assert which paths a real program executes. DLLs without an entry point need a dedicated fixture/root selection.

## Optional protected corpus

The six externally supplied inputs are not distributed. See [the corpus report](../docs/obfuscated-corpus.md) for pinned hashes and known gaps. For a matching local folder:

```powershell
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll corpus-info 'X:\samples'
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll corpus-check 'X:\samples'
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll corpus-probe 'X:\samples\input.exe' 06000001

dotnet .\validation\bin\Release\net10.0-windows\Validation.dll corpus-profile 'X:\samples\input.exe' 06000001
dotnet .\validation\bin\Release\net10.0-windows\Validation.dll corpus-match 'X:\samples\baseline.exe' 'X:\samples\input.exe'
```

`corpus-check` verifies hashes, selected known root relationships, candidate retention in the reported AntiDump/Form1 pointer tables and explicit incomplete boundaries. Passing it does not certify the complete graph. A probe/profile has a 40-second analysis cancellation deadline and a 500-node limit. Profiling reports analysis walks, not UI/decompiler loading or compositor performance. `corpus-match` compares unique retained metadata signatures only; it does not prove body equivalence or deanonymize fully renamed/virtualized methods. None of these commands execute the inputs.

## What passing does not establish

The generated arithmetic corpus and hand-written fixtures exercise supported semantics, not arbitrary program equivalence. Passing does not establish complete CLR/BCL models, universal deobfuscation, exploit-free parsing, native callback recovery, absence of all memory leaks, or full ECMA-335 conformance. Preserve and extend a failing fixture before fixing any newly reported graph error; do not change an expected edge merely to make a run pass.
