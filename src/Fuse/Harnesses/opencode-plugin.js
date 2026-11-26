// Written by `fuse init`; rerun it to update. Forwards OpenCode tool events to `fuse hook opencode`, or post-edit
// checks directly over the warm Fuse engine pipe for a sub-millisecond answer. Carries both plugin shapes:
// OpenCode 2 calls setup(), OpenCode 1 calls server().
import { spawn } from "node:child_process";
import net from "node:net";
import os from "node:os";
import path from "node:path";

const EDIT_TOOLS = new Set(["edit", "write", "multiedit", "patch", "apply_patch"]);
const SHELL_TOOLS = new Set(["shell", "bash"]);
const PATCH_HEADER_RE = /^\*\*\* (?:Add File|Update File|Delete File|Move to): (.+)$/gm;

// The same rule as PathRules.IsSource: the files the compiler reads as sources.
const SOURCE_EXTENSIONS = [".cs", ".razor", ".cshtml"];

function isSourceFile(file) {
  const lower = file.toLowerCase();
  return SOURCE_EXTENSIONS.some((ext) => lower.endsWith(ext));
}

const pipeCache = new Map();
let reqCounter = 0;

function pipeAddress(pipeName) {
  if (process.platform === "win32") return `\\\\.\\pipe\\${pipeName}`;
  return path.join(os.tmpdir(), `CoreFxPipe_${pipeName}`);
}

function runHook(event, payload, timeoutMs) {
  return new Promise((resolve) => {
    let out = "";
    let child;
    try {
      child = spawn("fuse", ["hook", "opencode", event], {
        cwd: payload.cwd || undefined,
        stdio: ["pipe", "pipe", "ignore"],
        windowsHide: true,
      });
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

function refreshPipeInfo(cwd) {
  if (!cwd) return Promise.resolve(null);
  const cached = pipeCache.get(cwd);
  if (cached && cached.pending) return cached.pending;
  if (cached && cached.info && Date.now() < cached.expiresAt) return Promise.resolve(cached.info);

  const pending = new Promise((resolve) => {
    let out = "";
    let child;
    try {
      child = spawn("fuse", ["status", "--json"], { cwd, stdio: ["ignore", "pipe", "ignore"], windowsHide: true });
    } catch {
      pipeCache.delete(cwd);
      return resolve(null);
    }
    const timer = setTimeout(() => { try { child.kill(); } catch {} }, 3000);
    child.on("error", () => { clearTimeout(timer); pipeCache.delete(cwd); resolve(null); });
    child.stdout.on("data", (chunk) => { out += chunk; });
    child.on("close", () => {
      clearTimeout(timer);
      try {
        const parsed = out.trim() ? JSON.parse(out) : null;
        if (parsed?.pipeName && parsed?.buildId) {
          const info = {
            pipePath: pipeAddress(parsed.pipeName),
            buildId: parsed.buildId,
            rootPath: cwd,
            running: parsed.engine === "running",
          };
          pipeCache.set(cwd, { info, expiresAt: Date.now() + 30000 });
          return resolve(info);
        }
      } catch {
      }
      pipeCache.delete(cwd);
      resolve(null);
    });
  });

  pipeCache.set(cwd, { info: cached?.info ?? null, pending, expiresAt: 0 });
  return pending;
}

function sendPipeRequest(pipePath, requestObj, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    let buffer = "";
    const socket = net.createConnection(pipePath);

    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { socket.destroy(); } catch {}
      resolve(value);
    };

    const timer = setTimeout(() => finish(null), timeoutMs);
    socket.on("error", () => finish(null));
    socket.on("connect", () => {
      socket.write(JSON.stringify(requestObj) + "\n");
    });
    socket.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      const nl = buffer.indexOf("\n");
      if (nl >= 0) {
        const line = buffer.slice(0, nl).trim();
        try {
          finish(line ? JSON.parse(line) : null);
        } catch {
          finish(null);
        }
      }
    });
    socket.on("end", () => {
      const line = buffer.trim();
      if (!line) return finish(null);
      try { finish(JSON.parse(line)); } catch { finish(null); }
    });
  });
}

function extractEditedFiles(cwd, input) {
  if (!input || typeof input !== "object") return [];
  const raw = [];
  for (const key of ["file_path", "filePath", "path", "notebook_path"]) {
    if (typeof input[key] === "string" && input[key]) raw.push(input[key]);
  }
  for (const value of Object.values(input)) {
    if (typeof value !== "string") continue;
    PATCH_HEADER_RE.lastIndex = 0;
    let m;
    while ((m = PATCH_HEADER_RE.exec(value)) !== null) {
      if (m[1]?.trim()) raw.push(m[1].trim());
    }
  }
  const base = cwd || process.cwd();
  return [...new Set(raw.map((p) => path.resolve(base, p)))];
}

function formatCheckReport(report) {
  if (!report || !report.errorCount) return null;
  const lines = [];
  for (const item of report.errors ?? []) {
    const err = item?.error;
    if (!err) continue;
    lines.push(`${err.path}(${err.line},${err.column}): error ${err.id}: ${err.message}`);
    if (item.cause) {
      const verb = item.cause.kind === "Removed" ? "removed" : "changed";
      lines.push(`  ${verb}: ${item.cause.declaration}`);
    }
  }
  const files = report.filesChecked ?? 0;
  const projects = (report.projects ?? []).join(", ");
  lines.push(`fuse: ${report.errorCount} error(s) introduced in ${report.errorFileCount ?? 0} file(s) (${projects}) (${files} file(s) checked)`);
  return lines.join("\n");
}

const rewrite = async (cwd, sessionID, input) => {
  if (typeof input?.command !== "string") return;
  const payload = { cwd, tool_input: input };
  if (typeof sessionID === "string" && sessionID) payload.session_id = sessionID;
  const answer = await runHook("pre-shell", payload, 10000);
  if (typeof answer?.command === "string") input.command = answer.command;
};

// The session id lets Fuse tell this session only the errors its own edits cause when other sessions edit the same
// working tree. A subagent runs in a child session with an id of its own.
const check = async (cwd, sessionID, input) => {
  const edited = extractEditedFiles(cwd, input);
  const sourceFiles = edited.filter(isSourceFile);
  if (edited.length > 0 && sourceFiles.length === 0) return null;

  const payload = { cwd, tool_input: input };
  if (typeof sessionID === "string" && sessionID) payload.session_id = sessionID;
  const session = typeof payload.session_id === "string" ? `opencode:${payload.session_id}` : undefined;
  const cached = pipeCache.get(cwd)?.info;
  if (cached && sourceFiles.length > 0) {
    const req = {
      request: "CheckFiles",
      buildId: cached.buildId,
      requestId: `oc-${process.pid}-${++reqCounter}`,
      waitMs: 50000,
      worktreeRoot: cached.rootPath,
      files: sourceFiles,
      waitForLoad: false,
      leaveOutCompilationWide: true,
      interactiveFast: true,
      ...(session ? { session } : {}),
    };
    const resp = await sendPipeRequest(cached.pipePath, req, 50000);
    if (resp?.status === "CheckAnswered" && resp.report) {
      const text = formatCheckReport(resp.report);
      if (resp.report.errorCount === 0 || text) {
        // The hook records the session's roots and written files before it checks; the pipe answer carries
        // neither, so with a session the hook still runs for its records while the pipe answer is returned.
        if (session) void runHook("post-edit", payload, 60000);
        return text;
      }
    }
    if (resp?.status === "Restart" || resp?.status === "Unanswered" || resp === null) {
      pipeCache.delete(cwd);
    }
  }

  const answer = await runHook("post-edit", payload, 60000);
  void refreshPipeInfo(cwd);
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
      // The stop hook checks every repository the session touched and applies the blocked-stop repeat detector;
      // the pipe answers one worktree only, so a clean pipe answer must not end the stop.
      const answer = await runHook("stop", { cwd, session_id: sessionID, stop_hook_active: active }, 300000);
      void refreshPipeInfo(cwd);
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
    void refreshPipeInfo(cwd);
    await ctx.tool.hook("execute.before", async (event) => {
      if (SHELL_TOOLS.has(event.tool)) await rewrite(cwd, event.sessionID, event.input);
    });
    await ctx.tool.hook("execute.after", async (event) => {
      if (event.status !== "completed" || !EDIT_TOOLS.has(event.tool)) return;
      const text = await check(cwd, event.sessionID, event.input);
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
    void refreshPipeInfo(directory);
    const stop = stopGuard(directory, (id, text) => client.session.prompt({ path: { id }, body: { parts: [{ type: "text", text }] } }));
    return {
      "tool.execute.before": async (input, output) => {
        if (SHELL_TOOLS.has(input.tool)) await rewrite(directory, input.sessionID, output.args);
      },
      "tool.execute.after": async (input, output) => {
        if (!EDIT_TOOLS.has(input.tool)) return;
        const text = await check(directory, input.sessionID, input.args);
        if (text) output.output = `${output.output ?? ""}\n\n${text}`;
      },
      event: async ({ event }) => {
        if (event.type === "session.idle") await stop(event.properties?.sessionID);
      },
    };
  },
};
