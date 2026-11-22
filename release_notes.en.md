# Call Graph v0.4.2

## Changes

### Call graphs

- Fixed cases where links between methods disappeared or a graph ended prematurely.
- Improved analysis of obfuscated code, virtual calls, delegates and function pointers. Unknown targets are now clearly indicated.
- Improved consistency between Callees, Callers and Both. All directions respect the selected analysis settings.

### Performance and display

- Faster graph construction and caller searches, especially when revisiting methods.
- Reduced delays when expanding branches, scrolling and zooming in Call Blocks and Function Map.
- Fixed card placement. Width adjusts automatically, and large methods use shorter previews; full code opens in dnSpy.

### Graph exploration

- Expand and collapse branches without changing the starting method. Pin cards and retain their positions.
- Added aliases, notes, method groups and call-site details explaining the links.
- Alt+Q opens the current method in the graph. Inside a graph, it starts exploring the selected or hovered card.

### File-size analysis

- Added Analyze file size for assemblies and modules: see the space used by sections, metadata, resources, classes and methods.
- The colored map shows the contents of the selected tree level. Drill down from a section to a class and method, inspect exact sizes and open code.
- Shared data is counted once. Unrecognized regions remain visible and are retained in the total size.

### Updates and interface

- Added automatic new-version notifications and update installation with a dnSpy restart. Failed updates support returning to the previous version.
- Simplified settings and removed duplicate panes. Windows follow dnSpy styling; the interface and release notes support English and Russian.
