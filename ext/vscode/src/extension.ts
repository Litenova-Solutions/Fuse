import * as vscode from "vscode";
import { SecretDiagnostics } from "./diagnostics/secrets";
import { GraphView } from "./graph/webview";
import { HostSupervisor } from "./host/supervisor";
import { FuseStatusBar } from "./statusBar";
import { Hotspot, HotspotsProvider } from "./views/hotspots";
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
  const secrets = new SecretDiagnostics(root);
  const graphView = new GraphView(context, root);

  context.subscriptions.push(
    output,
    statusBar,
    secrets,
    vscode.window.registerTreeDataProvider("fuse.indexStatus", indexStatus),
    vscode.window.registerTreeDataProvider("fuse.hotspots", hotspots),
    vscode.window.registerTreeDataProvider("fuse.scopeResult", scopeResult),
  );

  const hostPath = vscode.workspace.getConfiguration("fuse").get<string>("host.path", "");
  supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m));
  context.subscriptions.push({ dispose: () => supervisor?.dispose() });

  const indexCommand = vscode.commands.registerCommand("fuse.index", () => warmAndProject(root, statusBar, indexStatus, hotspots, secrets, output));
  const restartCommand = vscode.commands.registerCommand("fuse.restartHost", async () => {
    supervisor?.dispose();
    supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m));
    await warmAndProject(root, statusBar, indexStatus, hotspots, secrets, output);
  });
  context.subscriptions.push(indexCommand, restartCommand);

  // Scoping commands close the interactive loop: each runs a scoped fusion on the host, opens the payload
  // read-only, and populates the scope-result panel. They share one helper over the warm client.
  const runScope = async (mode: string, seed: string | null, query: string | null, since: string | null): Promise<void> => {
    if (!supervisor) {
      return;
    }
    try {
      const client = supervisor.connected ?? (await supervisor.start());
      const result = await client.scope(root, mode, seed, query, since, 50000);
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
        await graphView.show(client);
      } catch (err) {
        output.appendLine(`Fuse graph failed: ${String(err)}`);
        void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
      }
    }),
  );

  await warmAndProject(root, statusBar, indexStatus, hotspots, secrets, output);
}

// Starts (or reuses) the host, indexes the root, and projects the results into the status bar and trees.
async function warmAndProject(
  root: string,
  statusBar: FuseStatusBar,
  indexStatus: IndexStatusProvider,
  hotspots: HotspotsProvider,
  secrets: SecretDiagnostics,
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
    indexStatus.update([
      new StatusRow("State", index.indexState),
      new StatusRow("Files", String(index.fileCount)),
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

    // Surface secret findings as editor diagnostics (the C1 fix made visible).
    await secrets.refresh(client);
  } catch (err) {
    statusBar.setState("not indexed");
    output.appendLine(`Fuse indexing failed: ${String(err)}`);
    void vscode.window.showWarningMessage(`Fuse: ${String(err)}`);
  }
}

export function deactivate(): void {
  supervisor?.dispose();
  supervisor = undefined;
}
