import * as vscode from "vscode";

/** Per-file token cost and centrality, indexed by normalized repository-relative path, from the last graph. */
export interface FileMetric {
  tokenCost: number;
  centrality: number;
}

/**
 * A CodeLens at the top of each source file showing its Fuse token cost and graph centrality, drawn from the
 * dependency graph the extension already fetched. Toggled by the `fuse.showTokenLens` setting; the data is
 * pushed in by the extension after each graph load, so the lens never calls the host itself.
 */
export class TokenLensProvider implements vscode.CodeLensProvider {
  private readonly emitter = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.emitter.event;
  private metrics = new Map<string, FileMetric>();

  constructor(private readonly relativePathOf: (uri: vscode.Uri) => string) {}

  /** Replaces the per-file metrics (keyed by relative path) and refreshes the lenses. */
  update(metrics: Map<string, FileMetric>): void {
    this.metrics = metrics;
    this.emitter.fire();
  }

  provideCodeLenses(document: vscode.TextDocument): vscode.CodeLens[] {
    if (!vscode.workspace.getConfiguration("fuse").get<boolean>("showTokenLens", false)) {
      return [];
    }
    const metric = this.metrics.get(this.relativePathOf(document.uri));
    if (!metric) {
      return [];
    }
    const title = `Fuse: ~${formatTokens(metric.tokenCost)} tokens, centrality ${metric.centrality.toFixed(2)}`;
    return [new vscode.CodeLens(new vscode.Range(0, 0, 0, 0), { title, command: "" })];
  }
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : `${tokens}`;
}
