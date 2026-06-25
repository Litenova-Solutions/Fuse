import * as vscode from "vscode";
import { HostSupervisor } from "./host/supervisor";
import { FuseStatusBar } from "./statusBar";
import { Hotspot, HotspotsProvider } from "./views/hotspots";
import { IndexStatusProvider, StatusRow } from "./views/indexStatus";

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

  context.subscriptions.push(
    output,
    statusBar,
    vscode.window.registerTreeDataProvider("fuse.indexStatus", indexStatus),
    vscode.window.registerTreeDataProvider("fuse.hotspots", hotspots),
  );

  const hostPath = vscode.workspace.getConfiguration("fuse").get<string>("host.path", "");
  supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m));
  context.subscriptions.push({ dispose: () => supervisor?.dispose() });

  const indexCommand = vscode.commands.registerCommand("fuse.index", () => warmAndProject(root, statusBar, indexStatus, hotspots, output));
  const restartCommand = vscode.commands.registerCommand("fuse.restartHost", async () => {
    supervisor?.dispose();
    supervisor = new HostSupervisor(root, hostPath, (m) => output.appendLine(m));
    await warmAndProject(root, statusBar, indexStatus, hotspots, output);
  });
  context.subscriptions.push(indexCommand, restartCommand);

  await warmAndProject(root, statusBar, indexStatus, hotspots, output);
}

// Starts (or reuses) the host, indexes the root, and projects the results into the status bar and trees.
async function warmAndProject(
  root: string,
  statusBar: FuseStatusBar,
  indexStatus: IndexStatusProvider,
  hotspots: HotspotsProvider,
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
