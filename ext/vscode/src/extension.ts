import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { SecretDiagnostics } from "./diagnostics/secrets";
import { GraphView } from "./graph/webview";
import { HostSupervisor } from "./host/supervisor";
import { FuseHoverProvider } from "./hover/fuseHover";
import { FileMetric, TokenLensProvider } from "./lenses/tokenLens";
import { FuseStatusBar } from "./statusBar";
import { Hotspot, HotspotsProvider } from "./views/hotspots";
import { ExplainerProvider } from "./views/explainer";
import { IndexStatusProvider, StatusRow } from "./views/indexStatus";
import { ScopeResultProvider } from "./views/scopeResult";

let supervisor: HostSupervisor | undefined;

/**
 * Activates the extension: starts the warm Fuse host for the first workspace folder, then projects its index
 * state, token hotspots, and host health into the sidebar and status bar. Read-only in this phase; scoping
 * commands, the graph webview, and diagnostics build on the same host client.
 */
export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  if (!folder) {
    return;
  }
  const root = folder.uri.fsPath;

  const output = vscode.window.createOutputChannel("Fuse");
  const statusBar = new FuseStatusBar();
  const indexStatus = new IndexStatusProvider();
  const hotspots = new HotspotsProvider(root);
  const scopeResult = new ScopeResultProvider(root);
  const explainer = new ExplainerProvider(root);
  const secrets = new SecretDiagnostics(root);
  const graphView = new GraphView(context, root);
  const relativePathOf = (uri: vscode.Uri): string => vscode.workspace.asRelativePath(uri, false);
  const tokenLens = new TokenLensProvider(relativePathOf);
  const hover = new FuseHoverProvider(relativePathOf);

  context.subscriptions.push(
    output,
    statusBar,
    secrets,
    vscode.window.registerTreeDataProvider("fuse.indexStatus", indexStatus),
    vscode.window.registerTreeDataProvider("fuse.hotspots", hotspots),
    vscode.window.registerTreeDataProvider("fuse.scopeResult", scopeResult),
    vscode.window.registerTreeDataProvider("fuse.explainer", explainer),
    vscode.languages.registerCodeLensProvider({ scheme: "file", pattern: "**/*.cs" }, tokenLens),
    vscode.languages.registerHoverProvider({ scheme: "file", pattern: "**/*.cs" }, hover),
  );

  const configuredHostPath = vscode.workspace.getConfiguration("fuse").get<string>("host.path", "");
  const hostPath = resolveHostPath(context, configuredHostPath);
  // Re-project the warm engine into all surfaces. Used for the initial warm, the commands, and (via the
  // supervisor) the host's fuse/invalidated push when the workspace changes.
  const refresh = (): void => {
    void warmAndProject(root, statusBar, indexStatus, hotspots, secrets, tokenLens, hover, output);
  };
  supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m), refresh);
  context.subscriptions.push({ dispose: () => supervisor?.dispose() });

  const indexCommand = vscode.commands.registerCommand("fuse.index", refresh);
  const restartCommand = vscode.commands.registerCommand("fuse.restartHost", async () => {
    supervisor?.dispose();
    supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m), refresh);
    await warmAndProject(root, statusBar, indexStatus, hotspots, secrets, tokenLens, hover, output);
  });
  context.subscriptions.push(indexCommand, restartCommand);

  // The most recent scope, so "Show Dependency Graph" can overlay roles for what the last fusion included.
  let lastScope: { mode: string; seed: string | null; query: string | null; since: string | null } | undefined;

  // Scoping commands close the interactive loop: each runs a scoped fusion on the host, opens the payload
  // read-only, and populates the scope-result panel. They share one helper over the warm client.
  const runScope = async (mode: string, seed: string | null, query: string | null, since: string | null): Promise<void> => {
    if (!supervisor) {
      return;
    }
    try {
      const client = supervisor.connected ?? (await supervisor.start());
      const result = await client.scope(root, mode, seed, query, since, 50000);
      lastScope = { mode, seed, query, since };
      scopeResult.update(result.mode, result.files);
      if (result.payloadPath) {
        const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(result.payloadPath));
        await vscode.window.showTextDocument(doc, { preview: true });
      }
    } catch (err) {
      output.appendLine(`Fuse scope failed: ${String(err)}`);
      void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
    }
  };

  context.subscriptions.push(
    vscode.commands.registerCommand("fuse.search", async () => {
      const query = await vscode.window.showInputBox({ prompt: "Fuse: search the workspace", placeHolder: "what to find" });
      if (query) {
        await runScope("search", null, query, null);
      }
    }),
    vscode.commands.registerCommand("fuse.focusHere", async (resource?: vscode.Uri) => {
      const target = resource ?? vscode.window.activeTextEditor?.document.uri;
      if (target) {
        // The focus seed resolver matches a type name or file; the workspace-relative path resolves a file seed.
        await runScope("focus", vscode.workspace.asRelativePath(target), null, null);
      }
    }),
    vscode.commands.registerCommand("fuse.changesSince", async () => {
      const base = await vscode.window.showInputBox({ prompt: "Fuse: changes since which git base?", value: "main" });
      if (base) {
        await runScope("changes", null, null, base);
      }
    }),
    vscode.commands.registerCommand("fuse.showGraph", async () => {
      if (!supervisor) {
        return;
      }
      try {
        const client = supervisor.connected ?? (await supervisor.start());
        await graphView.show(client, lastScope);
      } catch (err) {
        output.appendLine(`Fuse graph failed: ${String(err)}`);
        void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
      }
    }),
    vscode.commands.registerCommand("fuse.explainScope", async () => {
      if (!supervisor) {
        return;
      }
      const query = await vscode.window.showInputBox({ prompt: "Fuse: explain the scope for which query?", placeHolder: "what to find" });
      if (!query) {
        return;
      }
      try {
        const client = supervisor.connected ?? (await supervisor.start());
        const result = await client.explain(root, "search", null, query, null);
        explainer.update(result.mode, result.files);
      } catch (err) {
        output.appendLine(`Fuse explain failed: ${String(err)}`);
        void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
      }
    }),
  );

  // Fire-and-forget: connecting to a cold host can take several seconds, so do not block activation on it. The
  // status bar shows progress and warmAndProject surfaces any failure through the output channel.
  void warmAndProject(root, statusBar, indexStatus, hotspots, secrets, tokenLens, hover, output);
}

// Starts (or reuses) the host, indexes the root, and projects the results into the status bar and trees.
async function warmAndProject(
  root: string,
  statusBar: FuseStatusBar,
  indexStatus: IndexStatusProvider,
  hotspots: HotspotsProvider,
  secrets: SecretDiagnostics,
  tokenLens: TokenLensProvider,
  hover: FuseHoverProvider,
  output: vscode.OutputChannel,
): Promise<void> {
  if (!supervisor) {
    return;
  }
  try {
    statusBar.setState("indexing...");
    const client = supervisor.connected ?? (await supervisor.start());

    const index = await client.index(root);
    const stats = await client.stats();
    const rss = `${Math.round(stats.workingSetBytes / (1024 * 1024))} MB`;
    statusBar.setState(`warm (${index.fileCount})`, `Fuse host ${stats.hostVersion}, RSS ${rss}`);
    const languages = index.languages.map((l) => `${l.language} ${l.count}`).join(", ");
    indexStatus.update([
      new StatusRow("Fuse version", index.fuseVersion),
      new StatusRow("State", index.indexState),
      new StatusRow("Tier", index.mode),
      new StatusRow("Files", String(index.fileCount)),
      new StatusRow("Symbols", String(index.symbolCount)),
      new StatusRow("Routes", String(index.routeCount)),
      new StatusRow("Languages", languages.length > 0 ? languages : "none"),
      new StatusRow("Full-text search", index.fullTextSearch ? "available" : "unavailable"),
      new StatusRow("Schema", `v${index.schemaVersion}`),
      new StatusRow("Index time", `${index.elapsedMs} ms`),
      new StatusRow("Host RSS", rss),
    ]);

    // Token hotspots: project the dependency graph's per-file token-cost estimates, most expensive first.
    const graph = await client.graph(root, "Files");
    const top = [...graph.nodes]
      .sort((a, b) => b.tokenCost - a.tokenCost)
      .slice(0, 50)
      .map((n) => new Hotspot(n.path, n.tokenCost));
    hotspots.update(top);

    // Feed the same graph metrics to the token CodeLens (per-file cost and centrality).
    const metrics = new Map<string, FileMetric>();
    for (const node of graph.nodes) {
      metrics.set(node.path, { tokenCost: node.tokenCost, centrality: node.centrality });
    }
    tokenLens.update(metrics);
    hover.update(metrics);

    // Surface secret findings as editor diagnostics (the C1 fix made visible).
    await secrets.refresh(client);
  } catch (err) {
    statusBar.setState("not indexed");
    output.appendLine(`Fuse indexing failed: ${String(err)}`);
    void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
  }
}

// Resolves the host executable: an explicit setting wins; otherwise a host bundled in the VSIX for this
// platform (host/<rid>/fuse[.exe], shipped by the platform-specific package) is preferred; otherwise the
// supervisor falls back to the `fuse` global tool on PATH. This keeps the base VSIX tiny and offline while
// letting a platform-specific build run without any global install.
function resolveHostPath(context: vscode.ExtensionContext, configured: string): string {
  if (configured) {
    return configured;
  }
  const rid = currentRid();
  if (rid) {
    const binary = process.platform === "win32" ? "fuse.exe" : "fuse";
    const bundled = path.join(context.extensionUri.fsPath, "host", rid, binary);
    if (fs.existsSync(bundled)) {
      return bundled;
    }
  }
  return ""; // empty -> supervisor uses `fuse` on PATH
}

// Maps the current platform and architecture to a Fuse runtime identifier, or undefined when unsupported.
function currentRid(): string | undefined {
  const os = process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : process.platform === "linux" ? "linux" : undefined;
  const arch = process.arch === "x64" ? "x64" : process.arch === "arm64" ? "arm64" : undefined;
  return os && arch ? `${os}-${arch}` : undefined;
}

export function deactivate(): void {
  supervisor?.dispose();
  supervisor = undefined;
}
