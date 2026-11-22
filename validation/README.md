# Validation

Requires Windows, a compatible .NET SDK and the target dnSpy `bin` directory.

```powershell
.\build.ps1 -dnspy_bin_dir 'C:\path\to\dnSpy\bin' -run_tests
```

Or run the validation host directly after building `validation/call_graph.validation.csproj` with `-p:DnSpyBinDir=...`:

```powershell
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll
```

The suite checks structural IL coverage, virtual/interface/generic dispatch, delegates, reflection, indirect calls, recursion, exceptions, initialization, cache invalidation, cancellation and continuation. WPF tests cover both graph views, dense links, viewport/card reuse, settings, localization and stale background results. Installer tests use disposable temporary hosts and never restart the user's dnSpy.

Regression checks also cover visible cross-links at depth boundaries, retained map coordinates when lower-token nodes are added, direction/layer changes, dense map spacing, candidate-aware snapshot cache limits and cancellation on cached refinement results.

File-size UI checks realize and lay out actual structure-row templates, verify one-way bindings to immutable row properties, and check that row swatches match stable, varied map colors. The window must contain only one structure table, without duplicate contents/methods controls. Merely checking list data would not catch WPF binding failures during cell creation.

Optional developer checks:

```powershell
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll --stability-audit 'C:\path\input.dll'
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll --direction-audit 'C:\path\input.dll'
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll --workspace-audit 'C:\path\input.dll' 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll'
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll --render-perf
dotnet validation/bin/Release/net10.0-windows/CallGraph.Validation.dll --size-audit 'C:\path\input.dll'
```

The structural audit checks every IL body without executing the input. The directional audit samples entry points, high-fan-in methods and indirect calls, checks that discovered forward edges remain available in reverse, and reports cold/warm query times. Generated tests additionally cover all three directions, cycles, metadata renaming, argument-dependent pointers, unknown remainders, view limits, invalidation and rapid WPF view switches.

The render benchmark reports CPU p50/p95 and current-thread allocations on generated graphs; it is not a GPU/FPS measurement or a substitute for a long dnSpy session. For consistent before/after measurements, use the same host/runtime and `DOTNET_TieredCompilation=0`.

Unit fixtures, test doubles and the `InternalsVisibleTo` test boundary are development infrastructure, not production UI features. Host binaries, generated images, build caches, local reports and old rollback scripts are not part of the source distribution. Passing tests does not prove complete static recovery of runtime-only targets.
