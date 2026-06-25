import * as net from "net";
import {
  createMessageConnection,
  MessageConnection,
  StreamMessageReader,
  StreamMessageWriter,
} from "vscode-jsonrpc/node";
import {
  DiagnosticsDto,
  ExplainResultDto,
  FuseHostHandshake,
  FuseHostStats,
  GraphDto,
  IndexResultDto,
  Methods,
  Notifications,
  ScopeResultDto,
} from "./protocol";

/**
 * A typed JSON-RPC client over the host transport (a named pipe on Windows, a Unix domain socket elsewhere).
 * One client per repository root; it connects to the address the host derives from that root.
 */
export class HostClient {
  private sessionToken: string | undefined;

  private constructor(private readonly connection: MessageConnection, private readonly socket: net.Socket) {}

  /** Connects to the host at the given transport address and starts listening. */
  static connect(endpoint: string): Promise<HostClient> {
    return new Promise((resolve, reject) => {
      const socket = net.connect(endpoint);
      socket.once("error", reject);
      socket.once("connect", () => {
        socket.removeListener("error", reject);
        const connection = createMessageConnection(
          new StreamMessageReader(socket),
          new StreamMessageWriter(socket),
        );
        connection.listen();
        resolve(new HostClient(connection, socket));
      });
    });
  }

  handshake(): Promise<FuseHostHandshake> {
    return this.connection.sendRequest<FuseHostHandshake>(Methods.handshake).then((result) => {
      this.sessionToken = result.sessionToken;
      return result;
    });
  }

  stats(): Promise<FuseHostStats> {
    return this.connection.sendRequest(Methods.stats, this.requireToken());
  }

  index(root: string): Promise<IndexResultDto> {
    return this.connection.sendRequest(Methods.index, this.requireToken(), root);
  }

  graph(
    root: string,
    detail: "Files" | "Directories",
    scope?: { mode: string; seed: string | null; query: string | null; since: string | null },
    directory?: string,
  ): Promise<GraphDto> {
    return this.connection.sendRequest(
      Methods.graph,
      this.requireToken(),
      root,
      detail,
      scope?.mode ?? null,
      scope?.seed ?? null,
      scope?.query ?? null,
      scope?.since ?? null,
      directory ?? null,
    );
  }

  scope(
    root: string,
    mode: string,
    seed: string | null,
    query: string | null,
    since: string | null,
    maxTokens: number,
  ): Promise<ScopeResultDto> {
    return this.connection.sendRequest(Methods.scope, this.requireToken(), root, mode, seed, query, since, maxTokens);
  }

  explain(root: string, mode: string, seed: string | null, query: string | null, since: string | null): Promise<ExplainResultDto> {
    return this.connection.sendRequest(Methods.explain, this.requireToken(), root, mode, seed, query, since);
  }

  diagnostics(root: string): Promise<DiagnosticsDto> {
    return this.connection.sendRequest(Methods.diagnostics, this.requireToken(), root);
  }

  /** Registers a handler for the host's `fuse/invalidated` notification (the workspace changed). */
  onInvalidated(handler: () => void): void {
    this.connection.onNotification(Notifications.invalidated, handler);
  }

  /** Asks the host to shut down (a notification: the host exits without a response). */
  shutdown(): void {
    if (!this.sessionToken) {
      return;
    }
    void this.connection.sendNotification(Methods.shutdown, this.sessionToken);
  }

  /** Whether the underlying socket is still connected. */
  get isConnected(): boolean {
    return !this.socket.destroyed && this.socket.readable;
  }

  dispose(): void {
    this.connection.dispose();
    this.socket.destroy();
  }

  private requireToken(): string {
    if (!this.sessionToken) {
      throw new Error("Fuse host session not established; call handshake first.");
    }
    return this.sessionToken;
  }
}
