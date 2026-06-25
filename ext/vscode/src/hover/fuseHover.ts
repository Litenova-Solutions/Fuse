import * as vscode from "vscode";
import { FileMetric } from "../lenses/tokenLens";

/**
 * A hover card over a source file showing its Fuse token cost and graph centrality, with a "Focus here" action
 * link. Reads the same per-file metrics the token lens uses (pushed in after each graph load), so hovering
 * never calls the host. The action runs the existing fuse.focusHere command on the hovered file.
 */
export class FuseHoverProvider implements vscode.HoverProvider {
  private metrics = new Map<string, FileMetric>();

  constructor(private readonly relativePathOf: (uri: vscode.Uri) => string) {}

  /** Replaces the per-file metrics (keyed by relative path). */
  update(metrics: Map<string, FileMetric>): void {
    this.metrics = metrics;
  }

  provideHover(document: vscode.TextDocument): vscode.Hover | undefined {
    const metric = this.metrics.get(this.relativePathOf(document.uri));
    if (!metric) {
      return undefined;
    }

    const focusArg = encodeURIComponent(JSON.stringify([document.uri]));
    const markdown = new vscode.MarkdownString(
      `**Fuse**: ~${formatTokens(metric.tokenCost)} tokens, centrality ${metric.centrality.toFixed(2)}\n\n` +
        `[Focus here](command:fuse.focusHere?${focusArg})`,
    );
    markdown.isTrusted = true;
    return new vscode.Hover(markdown);
  }
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : `${tokens}`;
}
