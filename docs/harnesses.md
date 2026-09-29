# Harnesses

This page is the reference for connecting Fuse to an agent: what `fuse init` detects, which file it writes for each harness, which hooks that file registers and what each hook answers, and how an MCP host without hooks runs `fuse mcp`. Read it after running `fuse init` in your repository, to check what it set up.

A harness is an agent host that runs Fuse's hooks: Claude Code, Cursor, Gemini CLI, Codex, GitHub Copilot CLI or OpenCode. `fuse init` registers hooks for every harness whose folder or file it finds in the repository root, and the MCP server for VS Code, which runs no hooks. When it finds none of them, it sets up Claude Code. Every hook calls `fuse hook <harness> <event>` with one of three events: `post-edit` checks the files an edit wrote, `pre-shell` rewrites `dotnet build` and `dotnet test` to `fuse build` and `fuse test`, and `stop` checks every change before the agent finishes. [Commands](commands.md#fuse-hook) describes what each event does.

In a repository with every harness's folder and `.vscode/`, `fuse init` writes seven files:

```text
$ fuse init
wrote .claude/settings.json
wrote .cursor/hooks.json
wrote .gemini/settings.json
wrote .codex/hooks.json
wrote .github/hooks/fuse.json
wrote .opencode/plugins/fuse.js
wrote .vscode/mcp.json
fuse: hooks registered; after each edit your agent gets the compiler errors the edit introduced, `dotnet test` runs the affected tests, and `dotnet build` prints only its errors
```

The configuration blocks on this page are the files that run wrote. Current builds write them as shown.

## Detection

| Harness | Detected by | File written | Events |
| --- | --- | --- | --- |
| Claude Code | `.claude/` or `CLAUDE.md`, or neither another harness nor `.vscode/` | `.claude/settings.json` | post-edit, pre-shell, stop |
| Cursor | `.cursor/` | `.cursor/hooks.json` | post-edit, stop |
| Gemini CLI | `.gemini/` or `GEMINI.md` | `.gemini/settings.json` | post-edit, pre-shell, stop |
| Codex | `.codex/` | `.codex/hooks.json` | post-edit, pre-shell, stop |
| GitHub Copilot CLI | `.github/copilot-instructions.md` or `.github/hooks/` | `.github/hooks/fuse.json` | post-edit, stop |
| OpenCode | `.opencode/`, `opencode.json` or `opencode.jsonc` | `.opencode/plugins/fuse.js` | post-edit, pre-shell, stop |
| VS Code agent mode | `.vscode/` | `.vscode/mcp.json` | none; the MCP server |

Cursor and GitHub Copilot CLI get no pre-shell hook, so there an agent's `dotnet build` and `dotnet test` run as they are, without the build lock or test selection. A repository with only `.vscode/` gets only `.vscode/mcp.json`, and no Claude Code settings.

## Claude Code

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude post-edit",
            "asyncRewake": true,
            "timeout": 300
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude pre-shell",
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude stop",
            "timeout": 300
          }
        ]
      }
    ]
  }
}
```

- **post-edit** runs in the background (`asyncRewake`), so it waits for the engine to finish loading. With errors introduced it writes them to standard error and exits with 2, which wakes the agent and shows it the text.
- **pre-shell** answers `{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{...}}}`, with every field of the original tool input and the rewritten `command`. It sets no permission decision, so the rewritten command goes through your normal permission rules.
- **stop** answers `{"decision":"block","reason":"..."}` with the errors, and prints nothing when there are none.

If the settings' `permissions.allow` list lets the agent run `dotnet build` or `dotnet test` without asking (`Bash(dotnet:*)`, `Bash(dotnet *)`, or a rule that starts with `Bash(dotnet build` or `Bash(dotnet test`), `fuse init` adds `Bash(fuse build:*)`, `Bash(fuse test:*)` or both, so the rewritten command is allowed too. A narrower rule, such as one for `dotnet test` on one project, still adds the rule for every `fuse test`.

Cursor also runs hooks from Claude Code's settings. A Claude Code hook whose payload comes from Cursor does nothing, so the Cursor hooks answer in a Cursor session; create `.cursor/` and run `fuse init` again to get them.

## Cursor

```json
{
  "version": 1,
  "hooks": {
    "postToolUse": [
      {
        "command": "fuse hook cursor post-edit",
        "matcher": "Write",
        "timeout": 60
      }
    ],
    "stop": [
      {
        "command": "fuse hook cursor stop",
        "timeout": 300
      }
    ]
  }
}
```

- **post-edit** answers `{"additional_context":"..."}`. It runs inline, so it does not wait for an engine that is still loading and waits up to 50 seconds for the answer.
- **stop** answers `{"followup_message":"..."}`, and `{}` when there are no errors.

## Gemini CLI

Gemini CLI timeouts are in milliseconds.

```json
{
  "hooks": {
    "AfterTool": [
      {
        "matcher": "write_file|replace",
        "hooks": [
          {
            "name": "fuse-check",
            "type": "command",
            "command": "fuse hook gemini post-edit",
            "timeout": 60000
          }
        ]
      }
    ],
    "BeforeTool": [
      {
        "matcher": "run_shell_command",
        "hooks": [
          {
            "name": "fuse-dotnet",
            "type": "command",
            "command": "fuse hook gemini pre-shell",
            "timeout": 10000
          }
        ]
      }
    ],
    "AfterAgent": [
      {
        "hooks": [
          {
            "name": "fuse-stop",
            "type": "command",
            "command": "fuse hook gemini stop",
            "timeout": 300000
          }
        ]
      }
    ]
  }
}
```

- **post-edit** answers `{"hookSpecificOutput":{"hookEventName":"AfterTool","additionalContext":"..."}}`, inline, like Cursor's.
- **pre-shell** answers `{"hookSpecificOutput":{"hookEventName":"BeforeTool","tool_input":{"command":"..."}}}`, which Gemini CLI merges over the tool's arguments.
- **stop** answers `{"decision":"deny","reason":"..."}`, and `{}` when there are no errors.

## Codex

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "^apply_patch$",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook codex post-edit",
            "timeout": 60
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "^Bash$",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook codex pre-shell",
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook codex stop",
            "timeout": 300
          }
        ]
      }
    ]
  }
}
```

- **post-edit** reads the edited files from the `apply_patch` patch and answers `{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"..."}}`, inline.
- **pre-shell** answers `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"command":"..."}}}`. Codex applies a rewritten command only together with an allow decision, which runs the command without asking you, so the hook answers only when the whole command is one `dotnet build` or `dotnet test` with plain arguments, such as `dotnet test --no-build`. Any other command, such as `cd Lib && dotnet build` or one with a pipe, a redirection, a quote, `$`, a backtick or a line break, gets no answer and goes through Codex's approval as the agent wrote it, without the build lock or test selection.
- **stop** answers `{"decision":"block","reason":"..."}`, and `{}` when there are no errors.

## GitHub Copilot CLI

```json
{
  "version": 1,
  "hooks": {
    "postToolUse": [
      {
        "type": "command",
        "matcher": "edit|create",
        "bash": "fuse hook copilot post-edit",
        "powershell": "fuse hook copilot post-edit",
        "timeoutSec": 60
      }
    ],
    "agentStop": [
      {
        "type": "command",
        "bash": "fuse hook copilot stop",
        "powershell": "fuse hook copilot stop",
        "timeoutSec": 300
      }
    ]
  }
}
```

- **post-edit** reads the tool arguments, which Copilot CLI may send as a JSON string, and answers `{"additionalContext":"..."}`, inline.
- **stop** answers `{"decision":"block","reason":"..."}`, and `{}` when there are no errors.

## OpenCode

OpenCode runs JavaScript plugins rather than commands, so `fuse init` writes `.opencode/plugins/fuse.js`, a plugin that starts `fuse hook opencode <event>` for each tool event and applies the answer. It carries both plugin formats: OpenCode 2 calls its `setup` function, and OpenCode 1 calls its `server` function.

- **post-edit** runs after the `edit`, `write`, `multiedit`, `patch` and `apply_patch` tools, and appends the errors to the tool's result, which the agent reads next.
- **pre-shell** runs before the `shell` and `bash` tools and sets the rewritten command on the tool's arguments.
- **stop** runs when a session finishes a turn (`session.execution.succeeded` in OpenCode 2, `session.idle` in OpenCode 1). With errors it sends the agent the stop hook's text as a prompt, once; the next finished turn of that session ends whatever the result.

The plugin allows 10 seconds for pre-shell, 60 for post-edit and 300 for stop, and treats a failure or a timeout as no answer.

## VS Code agent mode

VS Code runs no hooks, so `fuse init` registers the MCP server instead, and the agent calls `fuse_check`, `fuse_test` and `fuse_build` when it decides to:

```json
{
  "servers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": [
        "mcp"
      ],
      "cwd": "${workspaceFolder}"
    }
  }
}
```

## Other MCP hosts

Any MCP host can run the same server: the command `fuse` with the argument `mcp`, over standard input and output, with the repository as its working directory. The server finds the repository from its working directory on every call, so a host that starts servers elsewhere has to set the directory, as `cwd` does above. [Commands](commands.md#fuse-mcp) lists the tools, their arguments and a transcript.

## Running `fuse init` again

Running `fuse init` again updates the files in place:

- In the shared settings files (`.claude/settings.json`, `.cursor/hooks.json`, `.gemini/settings.json`, `.codex/hooks.json` and `.vscode/mcp.json`), it removes Fuse's entries, the handlers whose command starts with `fuse hook` and the `fuse` MCP server, and writes them again. Every other entry is kept. The file is read with comments and trailing commas allowed and written back as plain JSON, so comments are not kept.
- `.github/hooks/fuse.json` and `.opencode/plugins/fuse.js` belong to Fuse and are written whole.

For example, with a Claude Code settings file that holds a comment, a formatter hook and a `dotnet test` allowance:

```text
{
  // The team's formatter hook.
  "permissions": {
    "allow": ["Bash(dotnet test:*)"]
  },
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [{ "type": "command", "command": "dotnet format --include $CLAUDE_FILE_PATHS" }]
      }
    ]
  }
}
```

`fuse init` keeps the formatter hook, adds `Bash(fuse test:*)`, adds its own hooks after the others, and drops the comment:

```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet test:*)",
      "Bash(fuse test:*)"
    ]
  },
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "dotnet format --include $CLAUDE_FILE_PATHS"
          }
        ]
      },
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude post-edit",
            "asyncRewake": true,
            "timeout": 300
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude pre-shell",
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "fuse hook claude stop",
            "timeout": 300
          }
        ]
      }
    ]
  }
}
```

Run `fuse init` again after updating Fuse, so the settings and the OpenCode plugin match the installed version, and after adding a harness's folder to the repository.

## Tested versions

Fuse 5.0.0 was run by hand end to end with Claude Code 2.1.282 and with OpenCode 2.0.15. Every harness's answers and the files `fuse init` writes have unit tests; the Cursor, Gemini CLI, Codex and Copilot CLI adapters, and the OpenCode 1 plugin format, follow each harness's documented hook format, and the plugin's JavaScript has no tests of its own.
