import * as vscode from "vscode";

/** A flat tree of index-status rows (state, file count, host RSS), refreshed as the host reports progress. */
export class IndexStatusProvider implements vscode.TreeDataProvider<StatusRow> {
  private readonly emitter = new vscode.EventEmitter<StatusRow | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;
  private rows: StatusRow[] = [new StatusRow("State", "starting")];

  /** Replaces the displayed rows and refreshes the view. */
  update(rows: StatusRow[]): void {
    this.rows = rows;
    this.emitter.fire(undefined);
  }

  getTreeItem(element: StatusRow): vscode.TreeItem {
    const item = new vscode.TreeItem(`${element.label}: ${element.value}`, vscode.TreeItemCollapsibleState.None);
    return item;
  }

  getChildren(): StatusRow[] {
    return this.rows;
  }
}

/** One labeled status row. */
export class StatusRow {
  constructor(readonly label: string, readonly value: string) {}
}
