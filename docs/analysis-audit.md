# Analysis audit — 1.7.x

## 1.7.5: assembly scope and performance review

See [the 1.7.5 report](release-1.7.5.md) for the new persisted assembly filter, continuation/vector drawing optimizations, current test results and residual obfuscation gaps. Earlier results below remain historical; more passing fixtures do not establish universal completeness.

## 1.7.4: pointer-table follow-up

See [the pointer-table report](pointer-table-followup.md) for the reproduced AntiDump/Form1 gaps, bounded candidate identities, escape-aware invalidation, selector partitions and 30 new regressions. The portable suite totals 1,088 cases. A retained candidate is not a recovered runtime trace; the Armdot entry graph still reports 42 partially/fully unresolved indirect sites. Earlier version counts and corpus snapshots below are historical.

## 1.7.3: supplied protected corpus

See [the protected-input report](obfuscated-corpus.md) for six metadata-only input probes, retained-signature matching, fixed defects and unresolved delegate/VM boundaries. New checks cover opaque initializer defaults, empty entry bodies, addressable RVA data, initialized pointer tables and restricted floating constants. The portable suite now contains 1,058 cases; separate corpus subset and scale/WPF checks are not a completeness proof. No supplied executable or initializer was run. Previous version counts below are historical.

## 1.7.2: reproducible correctness and robustness follow-up

The source now includes [the validation project](../validation/README.md). It links the shipping analysis sources, has no private input paths, fails the process on a failed assertion and documents how to repeat core, generated and WPF checks. The plugin build explicitly excludes validation sources.

New adversarial checks reproduced incorrect exact pointer state after unknown/by-reference/bodyless calls, unknown memory writes and unfinished indirect-effect summaries. Such states now retain known candidates only with uncertainty instead of treating the old value as exact. A missing summary does not establish that a call has no effects. Supported no-effect Object constructor/GC.KeepAlive and Thread.IsBackground models avoid needlessly losing existing object/callback state.

Multiple possible `calli` targets now evaluate effects on isolated branch states and merge normal exits separately from exceptional exits. Known throwing indirect helpers no longer produce normal continuation edges. A known-null `callvirt`/`ldvirtftn` receiver does not invoke the referenced body. Unsupported helper operations mark the summary incomplete, and unsupported block/indirect writes invalidate exact memory assumptions.

The reverse index's direct-call fast path now checks stack depths, branch destinations and return shape. Instructions after `ret`, `throw` and `jmp` are excluded from structural reachability. Exception-bearing methods use the shared flow runner rather than independently seeding every handler as a call source. Invalid source-flow analysis discards already collected edges from that invalid method instead of publishing them as ordinary calls. These checks are structural, not full CLI type verification.

Validation of the validation assembly also exposed modified-void signatures: C# init-only setters use `void modreq(IsExternalInit)`. A raw ElementType comparison had introduced phantom return values and rejected valid methods. A shared modifier-aware return-value check now handles this in calls, indirect calls, returns, jumps and stack validation.

The reproducible core corpus contains **913 cases**: 145 focused positive/negative cases, 512 generated branch/calli graphs compared with the CLR's execution of that same generated test IL, and 256 mutations of eight malformed-IL templates. Fixed-seed generation makes failures repeatable. The CLR comparisons execute only the validation's own generated methods and harmless sentinels, never an inspected executable. The mutation suite is not a dnlib metadata-parser fuzzer.

Additional synthetic checks cover 100,000 methods, a 500-node graph, reverse indexing, progress and cancellation. WPF smoke checks exercise actual extension surfaces with generated nodes, placeholder snippets, light/dark resources, context actions, CFG cancellation and collection of 24 cleared surfaces. They do not measure a full dnSpy session, real decompilation throughput, compositor FPS or arbitrary theme compatibility.

A further metadata-only check of the ordinary input still finds all eight expected callbacks in both its primary loop and monitoring-thread body. Among visited methods, no resolvable internal ordinary-call/constructor sites were absent from the IL comparison. This is not an execution-path oracle or a completeness proof: the graph also carries unsupported-semantics notes. The protected input still has unresolved indirect sites and reaches the selected 500-node boundary. Its known entry/dispatcher/dead-branch/cached-delegate assertions are separate sample regressions, not evidence of universal deobfuscation.

The findings were substantive; the previous version was not free of incorrect edges. This follow-up improves its suitability for practical static analysis within the documented boundary. It does **not** justify describing every solid edge as inevitable runtime execution, every leaf as complete, or the implementation as universally exact, fully ECMA-conformant or exploit-free.

The core suite and generated WPF smoke check were run under both x64 and x86 .NET 10.0.12. Both runs passed; all 24 cleared surfaces were collectible in each UI run. This is not a claim that a complete x86 dnSpy application session or every third-party theme has been exercised.

Changed shipping files in 1.7.2: `Core/ContextCallEvaluator.cs`, `Core/ReachableCallAnalysis.cs`, `Core/InvocationModel.cs`, `Core/MethodFlowRunner.cs`, `Core/CallSignature.cs`, `Core/IlReachability.cs`, `Core/CallGraphIndex.cs`; new `Core/IlStackValidation.cs`. Build/docs: `CallGraph.Extension.csproj`, `README.md`, `CHANGELOG.md`, this document. Reproducible cases and instructions: `validation/`.

## 1.7.1 follow-up: ordinary calls lost after expensive helpers

An additional .NET Framework test input reproduced a serious coverage regression: the contextual summary of an early `RunChecks` call exhausted the same work counter used by its caller's source traversal. The caller then stopped before later, ordinary call sites. The graph contained only 46 nodes and 52 links despite a 2,000-node allowance.

The fix separates helper and source counters, bounds them independently, and retains a bounded structural call-discovery path for valid IL when source value-flow cannot finish. Recovery edges are possible, not exact; unexamined callee contexts do not inherit precise entry-field assumptions. Recovery does not follow address-only references or invent indirect targets. Invalid branch/stack structures do not trigger it. Cancellation is checked during structural scans.

The same input exposed callback retention across a shuffle and subsequent list copy. Unknown-index replacements now merge possible element values; incomplete helper effects keep known candidates with uncertainty. A copied collection with unknown count carries a candidate summary rather than discarding all contents. Standard threading delegate names are recognized within their framework assembly scope, allowing callback discovery without framework implementation bodies.

With filters disabled, maximum traversal depth and a 2,000-node allowance, the follow-up static check found 229 nodes and 328 links. Among the visited methods, no resolvable internal direct-call/constructor sites were absent in the comparison against IL. All eight expected callback methods were linked from both the main loop and its monitoring-thread body. This comparison is not a proof that every method in the assembly executes, nor that all dynamic targets or external frameworks have been reconstructed. The executable was never run.

Focused validation increased to **129 checks**: the original 64, 29 container/exception/delegate cases, and 36 IL/robustness cases. Added cases cover expensive repeated helper calls, an exhausted helper budget, exhausted-source structural recovery, address-only/dead-branch negatives during recovery, shuffle/copy retention, reversal and thread entry. The source traversal allows 300,000 symbolic steps globally and 32,768 per body (subject to the body-based budget); helper evaluation has a separate 750,000-step global allowance. Structural recovery is capped at 2,000,000 inspected instructions. Module-wide reverse indexing retains per-body limits rather than sharing these forward-view global counters.

This follow-up fixes a class of lost-call bugs without making the analyzer universally complete. In particular, an unresolved `calli` still requires a known pointer origin, and conservative recovery may include branches infeasible for a particular unknown input. Remaining limitations and the security boundary below still apply.

## Scope and interpretation

This review covers the extension's call-discovery pipeline, symbolic values and memory, contextual helper evaluation, workspace reverse index, and graph-facing cancellation/diagnostics. It does not certify the CLR, dnSpy, dnlib, the decompiler, WPF, or every ECMA instruction and metadata combination.

The reference is **ECMA-335**, not ECMA-355: [Common Language Infrastructure](https://ecma-international.org/publications-and-standards/standards/ecma-335/), especially Partition I execution state and Partition III instruction semantics. Checking selected semantics against a standard is not full conformance testing.

Call edges represent identified invocation sites. `ldftn`, `ldvirtftn`, and `ldtoken` alone do not invoke a method. A solid edge is not evidence that the program actually executed that site. Unknown input, runtime dispatch, exception paths, native code, and analysis bounds still require conservative results or explicit incomplete-analysis notes.

Call Blocks and Function Map consume the same call slice. CFG describes IL basic blocks within one method; its branches are not method-call edges and are not a runtime trace.

## Findings addressed

| Finding | Change | Validation |
| --- | --- | --- |
| Delegate targets lost through standard containers | Bounded List, tuple, key/value-pair, array and enumeration models | Eight-callback list and array loops, indexed reads, AddRange, replacements, Clear, RemoveAt, ToArray |
| Container construction confused with invocation | Keep creation/reference operations separate from delegate Invoke/calli | Negative list-only, method-address-only and subscription-only cases |
| Tuple copies/member addresses lost or stale | Immutable aggregate members and address writes; local/address-slot join reconciliation | Tuple copy and ref-member replacement |
| Unresolved helper calli could consume the stack twice | Complete the instruction's stack transfer once, including unknown results | Live pointer below an unresolved calli survives |
| Void indirect calls did not consistently apply writes | Evaluate supported void calli/delegate effects | Setter through calli and Action updates the next target |
| Multicast return treated as the union of callback returns | A known ordered list returns its last handler's result | Two pointer-returning handlers select only the last returned pointer |
| Exception handlers used entry-state values | Transfer conservative values at protected sites; normal finally continuations | Catch delegate, finally replacement, nested finally, throwing helper writes, no call after a known throwing helper |
| Handles could masquerade as callable pointers | Distinguish pointer, handle, reflection and delegate values | ldtoken/calli negative and valid method-handle conversion cases |
| Numeric width/truncation could prune the wrong branch | Explicit small-width conversions, storage/load extension and shift widths | Signed/unsigned byte/short, I4 truncation, I8 extension, 32/64-bit shifts and initlocals |
| calli ABI details ignored | Check calling convention and consume optional vararg arguments | Calling-convention mismatch and vararg fixtures |
| Large symbolic unions erased known targets | Retain bounded candidates and mark the unresolved remainder | 100-target union and 600-target capped union |
| Reverse index retained a base target superseded by devirtualization | Flow-sensitive virtual sites bypass the preliminary direct-only pass | Known override has no extra base edge in the reverse index |
| Names could be mistaken for framework/accessor identity | Check assembly scope for models/resolution and accessor metadata for filters | User System namespace, fake System.Delegate, get-prefixed ordinary method |
| Cancellation and malformed IL handling were inconsistent | Propagate traversal/CFG cancellation; check branches, stack and bounded state | Foreign branch, malformed switch, stack underflow/join/return, cancellation |

Framework assembly identity is an analysis assumption, **not cryptographic publisher authentication**. A forged identity or a replacement library can invalidate that assumption. The checks prevent simple name-only collisions; they are not a trust boundary.

## Limits and residual gaps

- A statically identified call site is not a record of execution. Arbitrary program reachability and dynamically generated targets cannot be reconstructed universally.
- Reflection, dynamic invocation, callbacks, delegates, collections and pointer arithmetic use supported models, not a complete BCL/CLR interpreter. Unsupported mutations or effects may force an unknown result.
- Exception transfer is conservative. Catch-type selection, unusual filters, mixed nested unwinding, fault handlers and asynchronous exceptions are not a fully verified CLR implementation. Possible exceptional-path edges can be over-approximated.
- Implicit `.cctor`/`beforefieldinit` timing and all resulting call edges are not completely represented.
- Arbitrary native-memory aliasing, raw memory copies, floating-point/checked numeric behavior, architecture-specific ABI tricks, explicit-this variants and every generic/variance combination are not fully modeled. Unsupported operations are reported where encountered; that does not prove that every possible modeling gap has been detected.
- VM bytecode/state, encrypted lookup tables, runtime-generated IL, unloaded assemblies and native callbacks can leave targets unresolved. An obfuscated input in the validation set still has unresolved indirect sites at the selected 500-node boundary.
- Filters, graph depth/node bounds, helper ambiguity/recursion/work budgets and symbolic/model size limits affect coverage. Do not interpret a leaf or a zero unresolved counter as a universal completeness proof.

Selected defensive bounds include 200,000 IL instructions per method, 128 exception handlers, 4,096 evaluation-stack entries, 32,768 modeled memory slots, 24 aggregate nesting levels, 512 modeled collection elements/retained method candidates, and 64 finally continuations. Bounds stop or approximate analysis and must not be described as successful reconstruction of the omitted remainder.

## Validation

The 1.7.0 review initially used a local harness with 121 checks. In 1.7.1 it grew to 129. The current 1.7.2 source includes the expanded reproducible validation project described above; earlier local counts are historical, not additional independent tests.

Additional checks exercise a generated 100,000-method module, traversal capped at 500 nodes, cancellation, WPF graph rendering with light/dark resource dictionaries, and collection of cleared graph surfaces. Synthetic traversal/index timings do not represent full interactive dnSpy loading, C# decompilation time, or compositor FPS. No actual x86 GUI session or exhaustive user-theme matrix is certified by an AnyCPU build.

Validation sources are published under `validation/`; their build output remains ignored and is not packaged with the plugin DLL. The former `tests/` and `artifacts/` exclusions remain unchanged. Publishing these cases makes the checks reproducible; it does not turn them into an exhaustive conformance or fuzz suite.

## Security boundary

The extension never intentionally invokes inspected methods. However, metadata parsing, resolution, decompilation and drawing run in dnSpy's process. The audit does not establish that those dependencies are free of vulnerabilities or that resource consumption is universally bounded. Exception handling, managed code and cancellation are not process isolation. Dedicated parser fuzzing, dependency security review, and an out-of-process worker would be separate hardening work. Analyze hostile binaries in an isolated environment.

## Files changed in this audit

- New: `Core/CollectionFlowModel.cs`, `Core/FrameworkIdentity.cs`, this document.
- Analysis: `Core/AdvancedCallAnalysis.cs`, `Core/ContextCallEvaluator.cs`, `Core/FlowMemory.cs`, `Core/IntegerExpression.cs`, `Core/InvocationModel.cs`, `Core/MethodFlowRunner.cs`, `Core/ReachableCallAnalysis.cs`, `Core/CallSignature.cs`, `Core/CallGraphIndex.cs`, `Core/GraphDiagnostic.cs`.
- Integration: `Core/ControlFlowGraph.cs`, `Controls/AnalysisDetails.cs`, `CallGraphControl.xaml.cs`.
- Release: `CallGraph.Extension.csproj`, `README.md`, `CHANGELOG.md`.
