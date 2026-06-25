import * as vscode from "vscode";
import { ExplainFileDto } from "../host/protocol";

/** A tree of the last explain result: each planned file with its role, reduction tier, and relevance score. */
export class ExplainerProvider implements vscode.TreeDataProvider<ExplainFileDto> {
  private readonly emitter = new vscode.EventEmitter<ExplainFileDto | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;
  private mode = "";
  private files: ExplainFileDto[] = [];

  constructor(private readonly workspaceRoot: string) {}

  /** Replaces the displayed plan (mode plus planned files) and refreshes the view. */
  update(mode: string, files: ExplainFileDto[]): void {
    this.mode = mode;
    this.files = files;
    this.emitter.fire(undefined);
  }

  getTreeItem(element: ExplainFileDto): vscode.TreeItem {
    const item = new vscode.TreeItem(element.path, vscode.TreeItemCollapsibleState.None);
    // Role and tier explain why the file is in and at what fidelity; score orders them.
    item.description = `${element.role} / ${element.tier} (${element.score.toFixed(2)})`;
    item.tooltip = `${this.mode}: role ${element.role}, tier ${element.tier}, score ${element.score}`;
    item.resourceUri = vscode.Uri.file(`${this.workspaceRoot}/${element.path}`);
    item.command = { command: "vscode.open", title: "Open", arguments: [item.resourceUri] };
    return item;
  }

  getChildren(): ExplainFileDto[] {
    return this.files;
  }
}
