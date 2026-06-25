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

  /** Refreshes the diagnostics for the whole workspace from the host. */
  async refresh(client: HostClient): Promise<void> {
    const result = await client.diagnostics(this.workspaceRoot);

    // Group findings by file, since a DiagnosticCollection is keyed per document URI.
    const byPath = new Map<string, vscode.Diagnostic[]>();
    for (const secret of result.secrets) {
      const range = new vscode.Range(secret.startLine, secret.startColumn, secret.endLine, secret.endColumn);
      const diagnostic = new vscode.Diagnostic(
        range,
        `Fuse: possible ${secret.kind} secret. It is redacted in fused output; remove it from source.`,
        vscode.DiagnosticSeverity.Warning,
      );
      diagnostic.source = "Fuse";
      const list = byPath.get(secret.path) ?? [];
      list.push(diagnostic);
      byPath.set(secret.path, list);
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
