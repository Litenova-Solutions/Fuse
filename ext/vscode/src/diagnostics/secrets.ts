import * as vscode from "vscode";
import { HostClient } from "../host/client";

/**
 * Surfaces the host's secret findings as editor diagnostics in a dedicated "Fuse: context" collection, never
 * mixed with compiler problems. Each finding underlines the exact literal range the host reported (the same
 * literal emitted output redacts), with the secret kind in the message, so the C1 redaction fix is visibly
 * correct in the editor and the Problems panel.
 */
export class SecretDiagnostics {
  private readonly collection: vscode.DiagnosticCollection;

  constructor(private readonly workspaceRoot: string) {
    this.collection = vscode.languages.createDiagnosticCollection("Fuse: context");
  }

  /** Refreshes the context diagnostics for the whole workspace from the host. */
  async refresh(client: HostClient): Promise<void> {
    const result = await client.diagnostics(this.workspaceRoot);

    // Group findings by file, since a DiagnosticCollection is keyed per document URI. Secrets are warnings
    // (precise ranges); token hotspots are informational (file-header range); graph gaps are hints.
    const byPath = new Map<string, vscode.Diagnostic[]>();
    const add = (path: string, diagnostic: vscode.Diagnostic): void => {
      diagnostic.source = "Fuse";
      const list = byPath.get(path) ?? [];
      list.push(diagnostic);
      byPath.set(path, list);
    };

    for (const secret of result.secrets) {
      const range = new vscode.Range(secret.startLine, secret.startColumn, secret.endLine, secret.endColumn);
      add(secret.path, new vscode.Diagnostic(
        range,
        `Fuse: possible ${secret.kind} secret. It is redacted in fused output; remove it from source.`,
        vscode.DiagnosticSeverity.Warning,
      ));
    }
    const header = new vscode.Range(0, 0, 0, 0);
    for (const hotspot of result.hotspots) {
      add(hotspot.path, new vscode.Diagnostic(
        header,
        `Fuse: token hotspot (~${formatTokens(hotspot.tokenCost)} tokens); a large share of any budget.`,
        vscode.DiagnosticSeverity.Information,
      ));
    }
    for (const gap of result.graphGaps) {
      add(gap, new vscode.Diagnostic(
        header,
        "Fuse: no dependency-graph edge to or from this file (possibly dead or reflection-only code).",
        vscode.DiagnosticSeverity.Hint,
      ));
    }

    this.collection.clear();
    for (const [path, diagnostics] of byPath) {
      this.collection.set(vscode.Uri.file(`${this.workspaceRoot}/${path}`), diagnostics);
    }
  }

  dispose(): void {
    this.collection.dispose();
  }
}

function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : `${tokens}`;
}
