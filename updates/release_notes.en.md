# Call Graph v0.4.2

## Changes

### Call analysis

- Separated structural IL references from indirect-target refinement. Discovered IL links are retained even when refinement lacks data.
- Improved analysis of virtual calls, delegates, function pointers and `calli`, including obfuscated code. Unknown targets remain explicit rather than being presented as resolved.
- Call-site evidence includes the IL offset, operation, candidate targets and resolution reasons. Address and argument sources can be explored through fields, writes, initializers and return values.

### Graph exploration

- Added branch expansion and collapse without changing the root, method pinning and expandable continuations beyond the current view.
- Added aliases, notes, reversible groups and compact cards. Card positions are saved for continued investigation.
- Full method code opens in the standard dnSpy decompiler; the Evidence pane shows the reasons behind selected calls.

### Performance and rendering

- Optimized layout, rendering, scrolling and zooming in both Call Blocks and Function Map.
- Calculations run in background workers; cards and links are applied in batches. Caching and data reuse reduce repeated work; stale background results are discarded.
- Improved card placement, automatic width with an upper bound and shortened previews for large methods.

### Interface

- Fixed Alt+Q shortcut: open a method in the graph, or explore the hovered or selected method card from within the graph.
- Removed unnecessary settings and duplicate panes. Low call-site counts no longer filter out links.
- Dialogs follow dnSpy styling. The interface and release notes support Russian and English.

### File-size analysis

- Added a size map from assembly and module context menus: PE sections, metadata, resources, method bodies and data outside sections.
- The map follows the selected tree level: file → sections → categories → namespaces → classes → methods. Colors match tree swatches; exact bytes, full paths and code navigation are available.
- Shared bodies are counted once; unknown and damaged regions remain in an explicit remainder. Size means the disk file, not memory or unsaved edits.

### Updates and reliability

- Added automatic update checks through Pastebin and notifications to download the DLL from GitHub. Failed installations support rollback; the backup DLL is removed after startup is acknowledged.
- Release notes are embedded in the DLL and shown once per installed version. Reopen them through Help → What's new in Call Graph.
- Removed temporary development tools and converted owned source identifiers to snake_case while preserving dnSpy/.NET contracts and saved data formats. Added analysis, rendering, settings and updater checks.

The graph represents static relationships, not an execution trace. Unknown runtime targets and view boundaries remain explicit; they do not mean that a method calls nothing.
