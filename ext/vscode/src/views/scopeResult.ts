import * as vscode from "vscode";
import { ScopeFileDto } from "../host/protocol";

/** A tree of the files the last scope included, each row showing token cost and opening the file when clicked. */
export class ScopeResultProvider implements vscode.TreeDataProvider<ScopeFileDto> {
  private readonly emitter = new vscode.EventEmitter<ScopeFileDto | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;
  private mode = "";
  private files: ScopeFileDto[] = [];

  constructor(private readonly workspaceRoot: string) {}

  /** Replaces the displayed scope (mode plus included files) and refreshes the view. */
  update(mode: string, files: ScopeFileDto[]): void {
    this.mode = mode;
    this.files = files;
    this.emitter.fire(undefined);
  }

  getTreeItem(element: ScopeFileDto): vscode.TreeItem {
    const item = new vscode.TreeItem(
      `${element.path} (~${formatTokens(element.tokenCost)})`,
      vscode.TreeItemCollapsibleState.None,
    );
    item.description = this.mode;
    item.resourceUri = vscode.Uri.file(`${this.workspaceRoot}/${element.path}`);
    item.command = { command: "vscode.open", title: "Open", arguments: [item.resourceUri] };
    return item;
  }

  getChildren(): ScopeFileDto[] {
    return this.files;
  }
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : `${tokens}`;
}
