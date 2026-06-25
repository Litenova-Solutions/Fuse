// Produces a platform-specific VSIX with a self-contained Fuse host bundled in. For a given .NET runtime
// identifier it publishes the host, copies it into host/<rid>/ (which the extension's resolveHostPath prefers
// over the PATH fallback), and runs `vsce package --target <vsceTarget>`. The host/ directory is gitignored and
// populated only at package time, so the source tree and the base no-host VSIX stay small.
//
// Usage: node scripts/package-platform.mjs <rid> <vsceTarget>
//   e.g. node scripts/package-platform.mjs win-x64 win32-x64

import { execFileSync } from "node:child_process";
import { cpSync, mkdirSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const [rid, vsceTarget] = process.argv.slice(2);
if (!rid || !vsceTarget) {
  console.error("Usage: node scripts/package-platform.mjs <rid> <vsceTarget>");
  process.exit(1);
}

const extDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(extDir, "..", "..");
const hostProject = join(repoRoot, "src", "Host", "Fuse.Cli", "Fuse.Cli.csproj");
const publishDir = join(repoRoot, "artifacts", "runtime", rid);
const bundleDir = join(extDir, "host", rid);

function run(cmd, args, cwd) {
  console.log(`> ${cmd} ${args.join(" ")}`);
  execFileSync(cmd, args, { cwd, stdio: "inherit", shell: process.platform === "win32" });
}

// 1. Publish the self-contained host for this RID (reuses the per-RID publish profile).
run("dotnet", ["publish", hostProject, "-c", "Release", `-p:PublishProfile=runtime-${rid}`], repoRoot);

// 2. Stage it where the extension looks for a bundled host (host/<rid>/).
rmSync(bundleDir, { recursive: true, force: true });
mkdirSync(bundleDir, { recursive: true });
cpSync(publishDir, bundleDir, { recursive: true });

// 3. Build the extension bundle and package the platform-specific VSIX.
run("npm", ["run", "build"], extDir);
run("npx", ["vsce", "package", "--target", vsceTarget, "--allow-missing-repository"], extDir);

console.log(`Packaged platform VSIX for ${vsceTarget} (host ${rid} bundled).`);
