import * as vscode from "vscode";
import { HostClient } from "../host/client";

/**
 * Hosts the dependency-graph webview: a single panel that loads the bundled Cytoscape renderer (no CDN, strict
 * CSP), fetches the graph from the host, and posts it to the webview. Clicking a node opens that file. The
 * level of detail starts at directories so a large repository ships a small graph.
 */
export class GraphView {
  private panel: vscode.WebviewPanel | undefined;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly workspaceRoot: string,
  ) {}

  /** Opens (or reveals) the graph panel and loads the graph from the host. */
  async show(client: HostClient): Promise<void> {
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel("fuse.graph", "Fuse: Dependency Graph", vscode.ViewColumn.Active, {
        enableScripts: true,
        localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, "dist")],
      });
      this.panel.onDidDispose(() => (this.panel = undefined));
      this.panel.webview.onDidReceiveMessage((message: { type: string; path?: string }) => {
        if (message.type === "open" && message.path) {
          void vscode.window.showTextDocument(vscode.Uri.file(`${this.workspaceRoot}/${message.path}`));
        }
      });
      this.panel.webview.html = this.html(this.panel.webview);
    }

    this.panel.reveal();
    const graph = await client.graph(this.workspaceRoot, "Directories");
    await this.panel.webview.postMessage({ type: "graph", graph });
  }

  // The webview HTML: a strict CSP that allows only the bundled script and inline styles, plus the graph host.
  private html(webview: vscode.Webview): string {
    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "dist", "webview.js"));
    const nonce = nonceString();
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="Content-Security-Policy"
    content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
  <style>
    html, body, #graph { height: 100%; width: 100%; margin: 0; padding: 0; }
    body { background: #1e1e1e; }
  </style>
</head>
<body>
  <div id="graph"></div>
  <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
  }
}

// A CSP nonce so only the bundled script runs; avoids Date/Math.random determinism concerns in the host process.
function nonceString(): string {
  let text = "";
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    text += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return text;
}
