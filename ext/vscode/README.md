# Fuse for VS Code

The human-facing twin of the Fuse MCP server. This extension hosts the Fuse engine warm in a background
process and projects its already-computed data into native VS Code surfaces: index status, token hotspots, a
dependency-graph webview, scoping commands, and secret diagnostics. The agent reads the warm engine over MCP;
the developer reads the same warm engine over a UI transport, sharing one index and one SQLite store.

## What it shows

- **Index panel** with the warm-index state, file count, index time, and host memory.
- **Token Hotspots** ranked by estimated token cost; click to open.
- **Scope Result** after a scoping command: the files a fusion includes, with token costs.
- **Dependency Graph** webview (Cytoscape, bundled, offline): nodes sized by centrality, colored by token cost.
- **Secret diagnostics**: detected secrets underlined at their exact range in the editor and Problems panel.

## Commands

- Fuse: Index Workspace
- Fuse: Search
- Fuse: Focus Here (editor and explorer context menu)
- Fuse: Changes Since Branch
- Fuse: Show Dependency Graph
- Fuse: Restart Host

## The host

The extension spawns `fuse host`, which serves the engine over a named pipe (Windows) or a Unix domain socket
(elsewhere). Set `fuse.host.path` to point at a specific `fuse` executable, or leave it empty to use the `fuse`
global tool on PATH. The host and engine run fully offline; no network and no telemetry.

## Offline and privacy

All assets (including the graph renderer) are bundled. The extension makes no network calls and sends no
telemetry. The optional dense-rerank model is never bundled and is only fetched by the explicit
`fuse models download` flow.
