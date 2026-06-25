// Bundles the extension into dist/extension.js with esbuild. No CDN, no network at runtime: every dependency
// (including vscode-jsonrpc) is bundled. The vscode module is provided by the host and is marked external.
const esbuild = require("esbuild");

const watch = process.argv.includes("--watch");

const options = {
  entryPoints: ["src/extension.ts"],
  bundle: true,
  outfile: "dist/extension.js",
  platform: "node",
  format: "cjs",
  target: "node20",
  external: ["vscode"],
  sourcemap: true,
  logLevel: "info",
};

async function main() {
  if (watch) {
    const ctx = await esbuild.context(options);
    await ctx.watch();
  } else {
    await esbuild.build(options);
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
