import { ChildProcess, spawn } from "child_process";
import { HostClient } from "./client";
import { endpointFor } from "./endpoint";
import { PROTOCOL_VERSION } from "./protocol";

/** A sink for human-readable supervisor log lines (wired to an output channel by the extension). */
export type LogSink = (message: string) => void;

/**
 * Spawns and supervises one `fuse host` process for a repository root: starts it, connects a client once the
 * transport is up, verifies the protocol version, and restarts with backoff if it dies. One supervisor per root.
 */
export class HostSupervisor {
  private process: ChildProcess | undefined;
  private client: HostClient | undefined;
  private disposed = false;
  private restarts = 0;

  constructor(
    private readonly root: string,
    private readonly hostPath: string,
    private readonly log: LogSink,
    private readonly onInvalidated?: () => void,
  ) {}

  /** The connected client, or undefined until {@link start} resolves. */
  get connected(): HostClient | undefined {
    return this.client;
  }

  /** Starts the host and connects. Throws if the host cannot be reached or the protocol version mismatches. */
  async start(): Promise<HostClient> {
    const executable = this.hostPath || "fuse";
    this.log(`Starting host: ${executable} host --directory ${this.root}`);
    this.process = spawn(executable, ["host", "--directory", this.root], { stdio: ["ignore", "pipe", "pipe"] });
    this.process.stderr?.on("data", (d: Buffer) => this.log(d.toString().trimEnd()));
    this.process.on("exit", (code) => this.onExit(code));

    const endpoint = endpointFor(this.root);
    const client = await this.connectWithBackoff(endpoint);

    const handshake = await client.handshake();
    if (handshake.protocolVersion !== PROTOCOL_VERSION) {
      client.dispose();
      throw new Error(
        `Fuse host protocol mismatch: host ${handshake.protocolVersion}, extension ${PROTOCOL_VERSION}. Update the Fuse host or extension.`,
      );
    }

    // Subscribe the fresh client to workspace-change notifications, so a restart re-establishes live refresh.
    if (this.onInvalidated) {
      client.onInvalidated(this.onInvalidated);
    }

    this.log(`Connected to Fuse host ${handshake.hostVersion} (protocol ${handshake.protocolVersion}).`);
    this.client = client;
    return client;
  }

  // Retries the connection while the freshly spawned host creates its transport, with a short linear backoff.
  private async connectWithBackoff(endpoint: string): Promise<HostClient> {
    let lastError: unknown;
    for (let attempt = 0; attempt < 30 && !this.disposed; attempt++) {
      try {
        return await HostClient.connect(endpoint);
      } catch (err) {
        lastError = err;
        await delay(100);
      }
    }
    throw new Error(`Could not connect to the Fuse host at ${endpoint}: ${String(lastError)}`);
  }

  private onExit(code: number | null): void {
    this.client = undefined;
    if (this.disposed) {
      return;
    }
    // Restart with capped backoff so a crashing host does not spin; give up after several attempts.
    if (this.restarts >= 5) {
      this.log(`Fuse host exited (code ${code}) and exceeded the restart limit; giving up.`);
      return;
    }
    const backoffMs = Math.min(5000, 250 * 2 ** this.restarts);
    this.restarts++;
    this.log(`Fuse host exited (code ${code}); restarting in ${backoffMs} ms (attempt ${this.restarts}).`);
    setTimeout(() => {
      if (!this.disposed) {
        this.start().catch((e) => this.log(`Restart failed: ${String(e)}`));
      }
    }, backoffMs);
  }

  dispose(): void {
    this.disposed = true;
    this.client?.shutdown();
    this.client?.dispose();
    this.process?.kill();
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
