import * as crypto from "crypto";
import * as os from "os";
import * as path from "path";

// Mirrors the .NET HostEndpoint so the extension connects to the address the host serves. The address is a
// stable function of the repository root: SHA-256 of the normalized, lowercased absolute path, first 8 bytes
// as lowercase hex. Keep this in lockstep with src/Host/Fuse.Cli/Host/Rpc/HostEndpoint.cs.
function rootHash(repositoryRoot: string): string {
  const normalized = path.resolve(repositoryRoot).replace(/[\\/]+$/, "").toLowerCase();
  const digest = crypto.createHash("sha256").update(normalized, "utf8").digest();
  return digest.subarray(0, 8).toString("hex");
}

/** The transport address (named pipe on Windows, Unix domain socket path elsewhere) for a repository root. */
export function endpointFor(repositoryRoot: string): string {
  const name = "fuse-host-" + rootHash(repositoryRoot);
  return process.platform === "win32"
    ? `\\\\.\\pipe\\${name}`
    : path.join(os.tmpdir(), `${name}.sock`);
}
