# Pointer-table follow-up — 1.7.4

## Reproduced defect

The supplied `crackme.armdot.exe` has two distinct pointer tables: AntiDump's table has 91 entries; Form1's table has 34 entries. The long 79-argument call in AntiDump reads the former, not the latter. Metadata was inspected without running the input, its type initializers or native imports.

In 1.7.3, a forward probe rooted at AntiDump (0x06000032) produced one node and no edges despite the `calli` at IL_016D. The analyzer could lose the array identity after an opaque effect, or suppress initialization information on a subsequent field load. An unsupported `localloc` was an additional analysis boundary. This was an analyzer limitation, not evidence that the program contained no invocation.

## Generic changes

- Symbolic values retain up to 64 alternative storage identities separately from an exact alias. Unknown effects preserve useful known candidates but clear exact scalar/reference assumptions.
- Static field loads can retain initializer candidates after a preceding opaque call. Array, object-field and indirect reads propagate candidate identities; writes through alternative aliases use weak updates.
- Unknown calls invalidate statics and heap objects reachable through statics, arguments or previously escaped references. They do not assume that an unrelated unescaped local array is externally accessible. Unknown by-reference and native-address effects remain conservative.
- Byte-offset writes invalidate overlapping modeled slots. Stack allocation supplies a symbolic address; this is not a complete model of zero-initialized bytes, native layout, pointer aliasing or arbitrary raw memory operations.
- Repeated array allocations use a separate uncertain summary instead of reusing an earlier exact object. Repeated stack allocations do not reuse an earlier exact address. Known null/out-of-bounds array access excludes normal continuation; exception handling remains conservative.
- Bodies of at most 512 IL instructions with recognized indirect-dispatch selectors retain up to eight selector/stack-constant partitions per program position. Other states merge under the existing work/size limits. This is bounded precision recovery, not unrestricted loop unrolling or a VM devirtualizer.
- Method context keys follow relevant static/object/array storage and argument addresses rather than including every unrelated array snapshot. Initialized array elements are indexed by owner; unchanged unknown-index reads can reuse a bounded cache. These optimizations do not certify end-to-end UI latency.
- Virtual function-pointer resolution preserves the unknown remainder. Restricted framework models cover runtime-type module retrieval, runtime-module name/handle queries, supported native handle casts and immutable string indexing. Unknown/custom virtual Module implementations are not assumed to behave like a runtime module. API contracts: [Type.Module](https://learn.microsoft.com/en-us/dotnet/api/system.type.module), [Module.FullyQualifiedName](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.module.fullyqualifiedname), [Marshal.GetHINSTANCE](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.gethinstance), [String.Chars](https://learn.microsoft.com/en-us/dotnet/api/system.string.chars).

The shipping core contains no sample hashes, tokens, obfuscator names or hard-coded table target lists.

## Interpretation and residual gaps

The reported table dispatchers now have outgoing indirect-target candidates instead of losing their tables entirely. A known index selects its entry; an unknown index or incomplete mutation state can retain several compatible known entries. Those uncertain links are dashed. Loading `ldftn` addresses in an initializer alone still does not create invocation edges.

The entry-root Armdot probe at a 500-node limit currently retains 137 nodes and 148 edges, versus the historical 45/44 snapshot. It **still reports 42 partially/fully unresolved indirect sites**. Known candidate recovery does not prove a complete target set or reconstruct a unique execution path. Merely counting more edges is not a correctness oracle. The AntiDump root retains 91 table candidates, not proof that all 91 execute in a particular run.

The bestfus 500-node frontier currently has 1,190 edges; selected entry targets, the repeated direct-call site, known dispatcher branch and exclusion of its alternate branches remain separate assertions. A bounded frontier can change when context propagation changes. The original corpus report preserves the historical 1.7.3 counts.

The ordinary-call regression input retains 229 nodes and 328 edges, with no resolvable internal ordinary-call sites absent among visited methods. This is not whole-program runtime coverage. Xerin's reflective proxy bindings and VMP's unavailable entry IL remain unreconstructed.

## Validation

The 30 `tables` checks cover lazy/materialized initialization, finite selector alternatives excluding an unselected third entry, 79 `ref` arguments and selector writes, address-only negatives, object/array joins, repeated allocations, array bounds/nulls, runtime-type versus ordinary object descriptors, escaped storage, overlapping writes, stack addresses, virtual-pointer uncertainty, immutable string indexing and cache invalidation. The existing unsupported-operation test now uses the still-unmodeled `ckfinite`; supported stack allocation has its own positive and negative checks.

The focused reverse-index regression now indexes only its Fixture type and nested helpers instead of incidentally indexing the whole analyzer/test harness. Separate 100,000-method checks retain index/traversal/cancellation coverage. Total portable coverage is 1,088 cases, not an ECMA conformance suite or parser security certification.

`corpus-check` additionally verifies candidate recovery in the two reported dispatchers and rejects unrelated targets at their indirect sites. These are selected relationship checks, not an independent oracle for all possible runtime paths. See [reproduction commands](../validation/README.md).

The complete portable suite and separate WPF smoke checks passed in x64 and x86 .NET 10.0.13 processes. The WPF checks used generated 500-node data, placeholder snippets and light/dark resource dictionaries; zero of 24 cleared surfaces remained retained after collection in either process. This is not a complete dnSpy host/FPS/user-theme or all-leaks certification. The pinned six-input subset and the two table probes also passed in both architectures. No inspected binary was executed.

## Changed files

Shipping: `Core/AdvancedCallAnalysis.cs`, `Core/ContextCallEvaluator.cs`, `Core/FlowMemory.cs`, `Core/InvocationModel.cs`, `Core/MethodFlowRunner.cs`, `CallGraph.Extension.csproj`.

Validation: `validation/TableFlowCoverage.cs` (new), `validation/Program.cs`, `validation/CorpusInspection.cs`, `validation/Regression.cs`, `validation/AuditIlCoverage.cs`, `validation/README.md`.

Documentation: `README.md`, `CHANGELOG.md`, `docs/analysis-audit.md`, this report. Graph appearance/settings and the removed context-menu submenus were not changed.
