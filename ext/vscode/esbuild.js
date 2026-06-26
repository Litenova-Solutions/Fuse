// Bundles the extension into dist/extension.js with esbuild. No CDN, no network at runtime: every dependency
// (including vscode-jsonrpc) is bundled. The vscode module is provided by the host and is marked external.
const esbuild = require("esbuild");

const watch = process.argv.includes("--watch");

// The extension host bundle (Node) and the graph webview bundle (browser, Cytoscape inlined). Both are fully
// self-contained: the webview bundle vendors Cytoscape so the panel needs no CDN and works offline.
const extensionOptions = {
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

const webviewOptions = {
  entryPoints: ["src/webview/graph.ts"],
  bundle: true,
  outfile: "dist/webview.js",
  platform: "browser",
  format: "iife",
  target: "es2020",
  sourcemap: true,
  logLevel: "info",
};

async function main() {
  if (watch) {
    const ext = await esbuild.context(extensionOptions);
    const web = await esbuild.context(webviewOptions);
    await Promise.all([ext.watch(), web.watch()]);
  } else {
    await Promise.all([esbuild.build(extensionOptions), esbuild.build(webviewOptions)]);
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
