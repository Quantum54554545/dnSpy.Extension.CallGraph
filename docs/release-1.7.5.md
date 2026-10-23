# 1.7.5 review and validation

The inspected project identified itself as 1.7.4 before this change. The review covers the available source and the supplied regression inputs; it is not a complete independent security or ECMA conformance audit.

## Assembly scope

Edit → Options → Call Graph now offers "Only the current assembly" in English/Russian according to the existing UI-language mechanism. It defaults to off, persists through dnSpy settings, and triggers the existing settings-change rebuild. When enabled, Call Blocks and Function Map keep method nodes belonging to the root method's assembly, including its additional modules. Matching namespaces/names do not make an external DLL part of that assembly. Modules without assembly metadata are scoped to the root module.

Hidden nodes are excluded from traversal/display; no direct caller-to-callee bypass edge is invented. A path that leaves the assembly and returns through a hidden DLL may therefore be absent. Direct calls and independently recovered callbacks from the displayed root remain eligible. The filter is a deliberate restricted view, not a claim that the application never calls libraries. External helper effects can still be evaluated to avoid changing call-target semantics. Enabling the filter does not avoid all workspace indexing/helper analysis costs.

CFG remains the IL control-flow graph of the selected method. Its call instructions are not erased by this setting, because erasing them would misrepresent the method.

## Optimizations

- A per-analysis continuation lookup scans a method backward once, rather than using IndexOf plus a forward scan at every call instruction. Cancellation remains checked during construction. This removes a quadratic lookup pattern without changing which instructions are considered continuations.
- Function Map already uses retained vector geometry. Previously a zoom delta above 0.001 forced a full drawing update. Small zoom changes now reuse the existing drawing via its matrix transform; scale-dependent strokes refresh when the ratio exceeds 1.3. This changes neither graph edges nor node visibility. In the smoke check, 50 small zoom steps cause one full drawing update per light/dark case. This is not measured compositor FPS or a promise of lag-free arbitrary graphs.

No claimed whole-program speedup is derived from synthetic timings. Call Blocks decompilation, large IL/VM evaluation, indexing and the host compositor remain separate costs.

## Correctness and obfuscation checks

- All 1,096 portable cases passed in x64 and x86 .NET 10.0.13: 200 focused checks, 640 generated CLR comparisons and 256 malformed-IL template cases. Only generated validation IL/sentinels execute.
- Eight new filter checks cover forward, Callers and Both traversal, default behavior, assembly versus namespace identity, multiple modules, modules without an assembly, and absence of invented bypass edges.
- Separate WPF checks cover the actual settings page's default/apply/in-memory persistence/reload/off behavior and filter propagation, map redraw reuse, generated 500-node surfaces, light/dark resources, CFG cancellation and collection of 24 cleared surfaces. They do not certify every user theme, complete host integration or all memory leaks.
- The six pinned protected inputs were inspected without running them. Selected corpus assertions and the reported 91/34-entry pointer-table probes pass in both architectures. Armdot still has 42 partially/fully unresolved indirect sites; bestfus has 44 at the selected 500-node frontier. Xerin's reflective proxy targets and VMP's missing entry IL remain unreconstructed. The improvements in this release do not resolve those existing gaps.
- The ordinary-call input retains 229 nodes and 328 links, with no resolvable internal direct-call sites absent among visited methods. Unsupported-semantics notes remain, so this is not an oracle for all runtime paths.

See [validation commands](../validation/README.md), the [protected-input report](obfuscated-corpus.md) and [pointer-table report](pointer-table-followup.md). An edge is a call-site relationship or a labeled candidate, not proof of inevitable runtime execution. Unknown boundaries must not be described as complete leaves.

## Source and release

Inspection of authored .cs, .xaml and .csproj files found no source comments to remove. URLs and IL diagnostic labels inside strings are not comments; license notices in LICENSE.txt remain intact. Generated obj/bin files are not authored source and are excluded from publication.

Shipping changes: CallGraphSettings.cs, UiText.cs, Core/CallGraphIndex.cs, Core/ReachableCallAnalysis.cs, Controls/FunctionMapSurface.cs and CallGraph.Extension.csproj. Validation changes: FilterCoverage.cs and SettingsCoverage.cs (new), Program.cs, UiCoverage.cs and validation/README.md. README.md, CHANGELOG.md, docs/analysis-audit.md and this report document the behavior.

The Release DLL is AnyCPU with Prefer32Bit disabled and no PDB. The release ZIP contains the extension, license, README/changelog, reports and screenshots; no inspected executables, SDKs, test outputs or source archives are bundled. The installed plugin is not overwritten by this release-build request.
