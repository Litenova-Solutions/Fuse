// Written by `fuse init`; rerun it to update. Forwards OpenCode tool events to `fuse hook opencode`, which answers in
// JSON. It carries both plugin shapes: OpenCode 2 calls setup(), OpenCode 1 calls server().
import { spawn } from "node:child_process";

const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "patch", "apply_patch"]);
const SHELL_TOOLS = new Set(["shell", "bash"]);

function fuse(event, payload, timeoutMs) {
  return new Promise((resolve) => {
    let out = "";
    let child;
    try {
      child = spawn("fuse", ["hook", "opencode", event], { stdio: ["pipe", "pipe", "ignore"], windowsHide: true });
    } catch {
      return resolve(null);
    }
    const timer = setTimeout(() => child.kill(), timeoutMs);
    child.on("error", () => { clearTimeout(timer); resolve(null); });
    child.stdout.on("data", (chunk) => { out += chunk; });
    child.on("close", () => {
      clearTimeout(timer);
      try { resolve(out.trim() ? JSON.parse(out) : null); } catch { resolve(null); }
    });
    child.stdin.on("error", () => {});
    child.stdin.end(JSON.stringify(payload));
  });
}

const rewrite = async (cwd, input) => {
  if (typeof input?.command !== "string") return;
  const answer = await fuse("pre-bash", { cwd, tool_input: input }, 10000);
  if (typeof answer?.command === "string") input.command = answer.command;
};

const check = async (cwd, input) => {
  const answer = await fuse("post-edit", { cwd, tool_input: input }, 60000);
  return typeof answer?.additionalContext === "string" ? answer.additionalContext : null;
};

// Before the turn ends, a check of every change; errors send the agent back once, as a Stop hook would. The next
// idle of the same session ends the turn whatever the result.
function stopGuard(cwd, prompt) {
  const continued = new Set();
  const checking = new Set();
  return async (sessionID) => {
    if (!sessionID || checking.has(sessionID)) return;
    checking.add(sessionID);
    try {
      const active = continued.delete(sessionID);
      const answer = await fuse("stop", { cwd, stop_hook_active: active }, 300000);
      if (answer?.decision !== "block" || typeof answer.reason !== "string") return;
      continued.add(sessionID);
      await prompt(sessionID, answer.reason);
    } catch {
      // A hook must never break the session.
    } finally {
      checking.delete(sessionID);
    }
  };
}

export default {
  id: "fuse",

  async setup(ctx) {
    const cwd = ctx.location.directory;
    await ctx.tool.hook("execute.before", async (event) => {
      if (SHELL_TOOLS.has(event.tool)) await rewrite(cwd, event.input);
    });
    await ctx.tool.hook("execute.after", async (event) => {
      if (event.status !== "completed" || !EDIT_TOOLS.has(event.tool)) return;
      const text = await check(cwd, event.input);
      if (text) event.result = { ...event.result, content: [...(event.result.content ?? []), { type: "text", text }] };
    });

    const stop = stopGuard(cwd, (sessionID, text) => ctx.session.prompt({ sessionID, text }));
    const abort = new AbortController();
    (async () => {
      try {
        for await (const event of ctx.event.subscribe({ signal: abort.signal })) {
          if (event.type === "session.execution.succeeded") stop((event.data ?? event.properties)?.sessionID);
        }
      } catch {
        // The stream ends when OpenCode shuts down.
      }
    })();
    return () => abort.abort();
  },

  async server({ client, directory }) {
    const stop = stopGuard(directory, (id, text) => client.session.prompt({ path: { id }, body: { parts: [{ type: "text", text }] } }));
    return {
      "tool.execute.before": async (input, output) => {
        if (SHELL_TOOLS.has(input.tool)) await rewrite(directory, output.args);
      },
      "tool.execute.after": async (input, output) => {
        if (!EDIT_TOOLS.has(input.tool)) return;
        const text = await check(directory, input.args);
        if (text) output.output = `${output.output ?? ""}\n\n${text}`;
      },
      event: async ({ event }) => {
        if (event.type === "session.idle") await stop(event.properties?.sessionID);
      },
    };
  },
};
