# Protected-input corpus — 1.7.3

Six supplied PE files were read as metadata. No input executable, initializer, delegate, VM payload or native import was run. Generated differential validation executes only the validation project's own IL. Filenames identify the supplied files; they do not independently establish which product/version/settings produced them.

## Method and bounds

The smallest input, `crackme.exe`, was used as a comparison baseline. Its original source/build provenance was not independently verified. All files declare assembly name `crackme`, version 1.0.0.0 and an AMD64, IL-only header; an IL-only flag does not establish that all runtime method bodies are statically available.

Entry-root forward probes used maximum traversal depth, no filters, a 500-node limit and a 40-second cancellation deadline. Call Blocks and Function Map share this discovery pipeline. CFG displays available IL within a method and does not decode VM bytecode. These are discovery checks, not end-to-end dnSpy UI timing measurements or a complete workspace reverse-index benchmark.

Unique retained metadata signatures were matched by declaring type, method name, static/instance shape, return type and parameter types, stripping outer custom modifiers. A match identifies retained metadata, not semantic equivalence of transformed implementations. Fully renamed methods are not guessed by similar-looking names, proximity or token order.

The [method-match CSV](corpus-method-matches.csv) records 85 such pairs across the five transformed inputs. In particular, Xerin preserves all 44 baseline signatures, while Armdot maps `anti_dump.AntiDump` from 0x06000009 to 0x06000032 and `Form1..ctor` from 0x0600000D to 0x060000AE. These are identifiers for inspection, not proof that the transformed bodies preserve their original behavior.

## Observations

| Input | Methods | IL instructions | Retained baseline signatures / 44 | 1.7.3 entry-root result | Residual boundary |
| --- | ---: | ---: | ---: | --- | --- |
| `crackme.exe` | 44 | 2,926 | 44 | 5 nodes, 4 edges | Native imports and framework/event-driven callbacks are not a complete runtime trace |
| `crackme.armdot.exe` | 436 | 43,091 | 12 | 45 nodes, 44 edges | Four entry `calli` targets retained; 42 indirect sites remain unresolved across the expanded graph |
| `crackme.bestfus.exe` | 2,870 | 573,563 | 0 | 500 nodes, 1,202 edges | Node boundary, work/semantic notes and 44 unresolved indirect sites |
| `crackme.HailHydra.exe` | 14,704 | 236,680 | 27 | 500 nodes, 501 edges | Wrapper expansion reaches node boundary; transcendental floating-point and other models remain incomplete |
| `crackme.xerin.exe` | 802 | 99,381 | 44 | 14 nodes, 13 edges | Five unresolved delegate-invocation notes; reflective/dynamically constructed proxy bodies were not recovered |
| `crackme.vmp.exe` | 9,885 | 12,129,488 | 2 | 1 entry node, 0 edges, explicit incomplete-body note | dnlib provides zero instructions for the entry body; original entry behavior remains unreconstructed |

Every result above is partial: `IsTruncated` includes incomplete semantic analysis, not only hitting a node boundary. An unresolved-calli count of zero does not establish completeness: Xerin has unresolved delegates, and VMP has no inspectable entry IL.

Armdot's 1.7.2 entry probe had no edges. The four recovered entry relationships are `calli` sites at IL_01F8, IL_027D, IL_02C9 and IL_0314, targeting 0x06000194–0x06000197 respectively. The table initializer stores four method pointers. Recovery retains the compatible known targets as candidates; unknown paths/remaining effects are not promoted to an execution trace. Loading those addresses in the initializer does not create invocation edges by itself.

Xerin's 1.7.2 probe stopped at two nodes and incorrectly treated the next delegate receiver as definitely null. A helper invoked by the delegate-holder initializer uses opaque reflection/dynamic construction; it cannot justify preserving untouched fields at their pre-initializer zero values. The fix removes that false-null conclusion and continues discovery. It does not claim to have reconstructed the dynamic proxy targets. The 44 metadata matches are not 44 proven runtime bindings.

The bestfus entry retains two direct targets (one called twice) and one recovered `calli` target. Its known constant dispatcher selects 0x06000AC5; other dispatcher branches are not added solely because their addresses appear in the file. Remaining indirect boundaries are still reported.

For VMP, probing `<Module>..cctor`, token 0x06000001, separately produced 165 nodes and 580 edges with incomplete-analysis notes. The initializer directly constructs 0x0600041D and calls VM dispatcher 0x06001913. That is the available initializer/dispatcher graph, not a reconstruction of the missing original entry body. The analysis did not unpack or execute the protected program.

## Generic fixes driven by these inputs

- Missing/empty IL bodies are explicit incomplete boundaries instead of apparently complete leaves. Empty CFG/code previews describe the missing IL.
- Opaque initializer effects no longer leave non-materialized static fields at a falsely precise default zero/null.
- Preserve supported address identity through `conv.i`/`conv.u` and constant byte offsets.
- Read bounded typed values from RVA field data on supported little-endian x86/x64 inputs; reject unknown width, out-of-range offsets, oversized field data and modeled modifications. This is not arbitrary native-memory emulation or a guarantee against runtime rewriting.
- Unknown array indices can retain finite pointer elements catalogued during initialization; current-state elements override catalogued entries.
- Track floating constants and exact representable arithmetic, plus supported constant Round/Truncate/Floor/Ceiling/Convert.ToInt32 patterns. Exactness uses rational checks; inexact arithmetic is not silently folded as exact. Transcendental functions and the full CLI floating-point/storage model remain outside this addition. CLI internal floating precision is implementation-dependent ([ECMA-335](https://ecma-international.org/publications-and-standards/standards/ecma-335/)); the restrictions are intentional.

No input hashes, obfuscator names, particular metadata tokens or sample-specific dispatch rules are used by the shipping core. Pinned corpus assertions belong only to `validation/CorpusInspection.cs`.

## Reproduction

See [validation instructions](../validation/README.md). `corpus-check` asserts a selected subset: known root targets, exclusion of the known bestfus dispatcher alternatives, unresolved Xerin status and the VMP empty-body boundary. It does not compare every edge against an independent whole-program oracle. There is no claim that every omitted IL site is a bug: dead branches, dispatch, exceptions and graph limits require case-by-case interpretation.

The portable suite now has 1,058 cases: 162 focused checks, 512 generated integer/calli comparisons, 128 exact floating-point comparisons and 256 malformed-IL cases. Separate scale/WPF checks and six pinned corpus subset checks supplement it. These checks are evidence for their covered cases, not universal deobfuscation or full ECMA conformance.

The complete portable suite, WPF smoke check and pinned six-input subset check passed under both x64 and x86 .NET 10.0.12. The WPF check retained zero of 24 cleared surfaces in each run. An earlier ordinary-call input was rechecked separately: all eight known callbacks still have links from both loops, with no resolvable internal direct-call sites absent among visited methods. None of this constitutes an exhaustive third-party corpus, full-host GUI test, memory-safety certification or runtime coverage proof.

### Pinned SHA-256

| Input | SHA-256 |
| --- | --- |
| `crackme.exe` | `9D997C3502E5F10AC6BB6C124386B0A5D31D2152ADF503F58FC30F028C63057B` |
| `crackme.armdot.exe` | `66F71223ECA0801D7AA75FAF1519810E86A5DCAD19B56D3BB12A16842AC529B6` |
| `crackme.bestfus.exe` | `526CD51BE23DAB2CAD96EDEDF9404CFEDFD68298D2567A8C723DBA47D4160C72` |
| `crackme.HailHydra.exe` | `6777E7CE2B6FABEEBA4BDE2B0B48FC08AE1663F1073EBF042B724372644C1FC8` |
| `crackme.xerin.exe` | `02381ADC8AC6BEF07808905F61D197082218BA4C24F6530876D09417AA4D809D` |
| `crackme.vmp.exe` | `549BCBF8829DB686169525EB3D0E4F9C0D5BC3D2D039635F00DAFFE812CC3482` |

## Changed files

Shipping core: `Core/AdvancedCallAnalysis.cs`, `Core/ContextCallEvaluator.cs`, `Core/FlowMemory.cs`, `Core/FloatingConstant.cs` (new), `Core/InvocationModel.cs`, `Core/ReachableCallAnalysis.cs`, `Core/ControlFlowGraph.cs`.

Integration/docs: `CallGraphControl.xaml.cs`, `UiText.cs`, `CallGraph.Extension.csproj`, `README.md`, `CHANGELOG.md`, `docs/analysis-audit.md`, this report.

Validation: `validation/CorpusInspection.cs`, `validation/ObfuscationCoverage.cs`, `validation/FloatingDifferential.cs` (new), `validation/Program.cs`, `validation/AuditIlCoverage.cs`, `validation/UiCoverage.cs`, `validation/README.md`.
