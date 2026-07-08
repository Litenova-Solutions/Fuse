import * as vscode from "vscode";
import { SessionListDto, SessionViewDto } from "../host/protocol";
import {
  buildSessionChildren,
  buildSessionRows,
  SessionChildRow,
  SessionRow,
} from "./sessionModel.js";

/** The subset of the host client the panel needs: the two read-only session-observability calls (G3). */
export interface SessionsClient {
  sessions(root: string): Promise<SessionListDto>;
  sessionView(root: string, session: string): Promise<SessionViewDto>;
}

type SessionNode = SessionRow | SessionChildRow;

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
      case "session": {
        const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = node.description;
        item.iconPath = new vscode.ThemeIcon("pulse");
        item.contextValue = "fuseSession";
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
        return buildSessionRows(await client.sessions(this.root));
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
