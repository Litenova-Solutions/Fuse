import * as vscode from "vscode";

/** A tree of the most token-expensive files, each row opening the file when clicked. */
export class HotspotsProvider implements vscode.TreeDataProvider<Hotspot> {
  private readonly emitter = new vscode.EventEmitter<Hotspot | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;
  private hotspots: Hotspot[] = [];

  constructor(private readonly workspaceRoot: string) {}

  /** Replaces the hotspot list (path plus token cost) and refreshes the view. */
  update(hotspots: Hotspot[]): void {
    this.hotspots = hotspots;
    this.emitter.fire(undefined);
  }

  getTreeItem(element: Hotspot): vscode.TreeItem {
    const item = new vscode.TreeItem(
      `${element.path} (~${formatTokens(element.tokenCost)})`,
      vscode.TreeItemCollapsibleState.None,
    );
    item.resourceUri = vscode.Uri.file(`${this.workspaceRoot}/${element.path}`);
    item.command = {
      command: "vscode.open",
      title: "Open",
      arguments: [item.resourceUri],
    };
    return item;
  }

  getChildren(): Hotspot[] {
    return this.hotspots;
  }
}

/** One token hotspot: a file path and its estimated token cost. */
export class Hotspot {
  constructor(readonly path: string, readonly tokenCost: number) {}
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : `${tokens}`;
}
