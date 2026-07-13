// Reproducible driver for the README terminal demo (marketing plan M4).
//
// Spawns `fuse mcp serve`, calls the real fuse_check MCP tool against the in-repo
// OrderingApp fixture with a proposed edit that misuses an API (a member that does not
// exist), then with the corrected edit, and writes an asciinema v2 .cast whose output
// blocks are the tool's REAL responses (never fabricated). Render the cast to the GIF with
// agg (see assets/demo/README.md).
//
//   node assets/demo/fuse-check-demo.mjs [--fuse <path>] [--fixture <path>] [--cast <path>]
//
// Defaults: --fuse fuse (must be v4), --fixture tests/fixtures/OrderingApp, --cast
// assets/demo/fuse-check-demo.cast. Requires a v4 `fuse` on PATH or a built binary passed
// with --fuse. The output is oracle grade when a build capture is available for the fixture,
// otherwise build grade; either way the response text is real.
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..');

function arg(name, dflt) {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}
const FUSE = arg('--fuse', 'fuse');
const FIXTURE = resolve(arg('--fixture', join(REPO, 'tests', 'fixtures', 'OrderingApp')));
const CAST = resolve(arg('--cast', join(HERE, 'fuse-check-demo.cast')));
const REL = 'Ordering/OrderService.cs';

// The proposed edit. The broken form references OrderOptions.MaxItemCount, which does not
// exist (the member is MaxItems); the fixed form uses MaxItems. Both are fully documented so
// the only diagnostic is the intended one.
const BROKEN = `using Microsoft.Extensions.Options;

namespace OrderingApp.Ordering;

/// <summary>Caps an order quantity at the configured maximum.</summary>
public sealed class OrderService : IOrderService
{
    private readonly OrderOptions _options;

    /// <summary>Creates the service from bound <see cref="OrderOptions"/>.</summary>
    public OrderService(IOptions<OrderOptions> options) => _options = options.Value;

    /// <summary>Returns the quantity, capped at the configured maximum.</summary>
    public int Create(int quantity) => System.Math.Min(quantity, _options.MaxItemCount);
}
`;
const FIXED = BROKEN.replaceAll('MaxItemCount', 'MaxItems');

// --- MCP stdio client (newline-delimited JSON-RPC) ---
const child = spawn(FUSE, ['mcp', 'serve'], { cwd: REPO, stdio: ['pipe', 'pipe', 'ignore'] });
let buf = '';
const pending = new Map();
child.stdout.on('data', (d) => {
  buf += d.toString();
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  }
});
let idc = 0;
const rpc = (method, params) => new Promise((res) => {
  const id = ++idc;
  pending.set(id, res);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
});
const notify = (method, params) =>
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
const textOf = (r) => (Array.isArray(r?.result?.content)
  ? r.result.content.map((x) => x.text ?? '').join('\n') : JSON.stringify(r));

const check = async (content) => textOf(await rpc('tools/call', {
  name: 'fuse_check', arguments: { path: FIXTURE, file: REL, content, analyzers: false },
}));

// --- cast builder (asciinema v2) ---
const COLS = 100, ROWS = 30;
const events = [];
let t = 0;
const R = '[0m';
const PROMPT = `[1;38;5;141m~/OrderingApp[0m $ `;
const DIM = (s) => `[38;5;245m${s}${R}`;
const emit = (s) => events.push([Number(t.toFixed(3)), 'o', s]);
const pause = (dt) => { t += dt; };
const nl = () => emit('\r\n');
const type = (s, cps = 32) => { for (const ch of s) { emit(ch); t += 1 / cps; } };
const say = (s) => { emit(s); nl(); t += 0.05; };
const block = (text, lead = 0.9) => { pause(lead); for (const ln of text.split('\n')) say(ln); };
const cmd = (s) => { emit(PROMPT); type(s); nl(); };

async function run() {
  await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'demo', version: '1' } });
  notify('notifications/initialized', {});
  await check(FIXED);                 // warm the fixture (off camera)
  const broken = (await check(BROKEN)).trimEnd();
  const fixed = (await check(FIXED)).trimEnd();
  child.kill();

  console.log('--- BROKEN ---\n' + broken + '\n--- FIXED ---\n' + fixed);

  // Compose the terminal narrative around the REAL responses.
  pause(0.7);
  say(DIM('# An AI agent is about to edit OrderService.cs. Before it writes the file,'));
  say(DIM('# it asks the compiler through Fuse whether the edit is valid.'));
  nl();
  pause(1.4);
  cmd('fuse_check file="Ordering/OrderService.cs" \\');
  emit('           '); type('content="... System.Math.Min(quantity, _options.MaxItemCount);"'); nl();
  block(broken, 1.3);
  nl();
  pause(3.0);
  say(DIM('# Caught before the edit landed, with the fix named. The agent applies the'));
  say(DIM('# repair packet (MaxItemCount -> MaxItems) and re-checks.'));
  nl();
  pause(1.5);
  cmd('fuse_check file="Ordering/OrderService.cs" \\');
  emit('           '); type('content="... System.Math.Min(quantity, _options.MaxItems);"'); nl();
  block(fixed, 1.2);
  nl();
  pause(1.2);
  say(DIM('# Verified. No dotnet build round-trip, and nothing was written to disk.'));
  pause(2.6);

  const header = { version: 2, width: COLS, height: ROWS, env: { TERM: 'xterm-256color', SHELL: '/bin/bash' } };
  const out = [JSON.stringify(header), ...events.map((e) => JSON.stringify(e))].join('\n') + '\n';
  writeFileSync(CAST, out, 'utf8');
  console.log('wrote', CAST, '(' + events.length + ' events, ' + t.toFixed(1) + 's)');
  process.exit(0);
}
run();
