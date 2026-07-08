import * as vscode from "vscode";
import { SessionDiffDto, SessionListDto, SessionViewDto } from "../host/protocol";
import {
  buildSessionChildren,
  buildSessionRows,
  buildWorktreeChildren,
  SessionChildRow,
  SessionRow,
  WorktreeChildRow,
  WorktreeRow,
  WORKTREE_ROW,
} from "./sessionModel.js";

/** The subset of the host client the panel needs: the read-only session-observability calls (G3, G3b). */
export interface SessionsClient {
  sessions(root: string): Promise<SessionListDto>;
  sessionView(root: string, session: string): Promise<SessionViewDto>;
  sessionDiff(root: string): Promise<SessionDiffDto>;
}

type SessionNode = WorktreeRow | SessionRow | SessionChildRow | WorktreeChildRow;

/**
 * The agent observability panel (G3): a read-only tree of the sessions the host knows for the workspace, each
 * expanding to the diagnostics its edits introduced or resolved since its baseline and its graded claim ledger.
 * It is strictly a window onto what the agent is doing - no promote or discard actions (those need F1). Session
 * rows are fetched lazily from the host when the node expands, so the panel reflects the live state on refresh.
 */
export class SessionsProvider implements vscode.TreeDataProvider<SessionNode> {
  private readonly emitter = new vscode.EventEmitter<SessionNode | undefined>();
  readonly onDidChangeTreeData = this.emitter.event;

  constructor(private readonly root: string, private readonly clientOf: () => SessionsClient | undefined) {}

  /** Refreshes the panel (re-fetches the session list and any expanded views). */
  refresh(): void {
    this.emitter.fire(undefined);
  }

  getTreeItem(node: SessionNode): vscode.TreeItem {
    switch (node.kind) {
      case "worktree": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Collapsed);
        item.iconPath = new vscode.ThemeIcon("git-compare");
        item.contextValue = "fuseWorktree";
        return item;
      }
      case "session": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = node.description;
        item.iconPath = new vscode.ThemeIcon("pulse");
        item.contextValue = "fuseSession";
        return item;
      }
      case "difffile": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
        item.description = node.description;
        item.iconPath = new vscode.ThemeIcon("diff");
        item.resourceUri = vscode.Uri.file(`${this.root}/${node.path}`);
        item.command = { command: "vscode.open", title: "Open", arguments: [item.resourceUri] };
        return item;
      }
      case "diagnostic": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
        item.description = node.description;
        item.iconPath = new vscode.ThemeIcon("error");
        if (node.path) {
          item.resourceUri = vscode.Uri.file(`${this.root}/${node.path}`);
          item.command = { command: "vscode.open", title: "Open", arguments: [item.resourceUri] };
        }
        return item;
      }
      case "claims": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
        item.tooltip = node.tooltip;
        item.iconPath = new vscode.ThemeIcon("law");
        return item;
      }
      default: {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.None);
        item.iconPath = new vscode.ThemeIcon("info");
        return item;
      }
    }
  }

  async getChildren(element?: SessionNode): Promise<SessionNode[]> {
    const client = this.clientOf();
    if (!client) {
      return [];
    }
    try {
      if (!element) {
        // The working-tree diff is a workspace-global root node (G3b), a sibling of the per-session rows.
        return [WORKTREE_ROW, ...buildSessionRows(await client.sessions(this.root))];
      }
      if (element.kind === "worktree") {
        return buildWorktreeChildren(await client.sessionDiff(this.root));
      }
      if (element.kind === "session") {
        return buildSessionChildren(await client.sessionView(this.root, element.sessionId));
      }
      return [];
    } catch {
      // The host may be starting or gone; an empty panel is the honest read-only state, never an error dialog.
      return [];
    }
  }
}
