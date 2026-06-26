import * as vscode from "vscode";

/** The persistent Fuse status bar item: shows index state and, in its tooltip, host RSS and cache health. */
export class FuseStatusBar {
  private readonly item: vscode.StatusBarItem;

  constructor() {
    this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    this.item.command = "fuse.index";
    this.setState("starting");
    this.item.show();
  }

  /** Sets the visible state label (for example "warm (266)" or "indexing..."). */
  setState(label: string, tooltip?: string): void {
    this.item.text = `$(symbol-misc) Fuse: ${label}`;
    this.item.tooltip = tooltip ?? "Fuse warm engine";
  }

  dispose(): void {
    this.item.dispose();
  }
}
