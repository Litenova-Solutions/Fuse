using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Cli;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Hooks;

/// <summary>
///     <c>fuse hook &lt;harness&gt; &lt;event&gt;</c>: the one entry point every installed hook calls. It reads the harness's
///     JSON from stdin and answers in that harness's format.
/// </summary>
/// <remarks>
///     A hook must never break the agent's session, so a failure inside Fuse ends with exit code 0 and no output
///     (logged to <c>hook.log</c> in <see cref="RepoRoot.StateDirectory"/>). Only new errors and a missing restore produce
///     output; in Claude Code they come with exit code 2, which wakes the agent.
/// </remarks>
internal static class HookCommand
{
    private static readonly string[] Harnesses = ["claude", "cursor", "gemini", "codex", "copilot", "opencode"];

    // Diagnostics are full of quotes and angle brackets; relaxed escaping keeps the JSON readable in harness logs.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || !Harnesses.Contains(args[0]) || args[1] is not ("post-edit" or "pre-bash" or "stop"))
        {
            await Console.Error.WriteLineAsync("usage: fuse hook <claude|cursor|gemini|codex|copilot|opencode> <post-edit|pre-bash|stop>").ConfigureAwait(false);
            return 0;
        }

        var harness = args[0];
        var hookEvent = args[1];
        HookPayload payload;
        try
        {
            payload = HookPayload.Parse(await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return 0;
        }

        // Cursor also runs hooks from Claude Code's settings; the Cursor-native hook handles that session.
        if (harness == "claude" && payload.FromCursor)
            return 0;

        try
        {
            return hookEvent switch
            {
                "pre-bash" => PreBash(harness, payload),
                "post-edit" => await PostEditAsync(harness, payload, cancellationToken).ConfigureAwait(false),
                _ => await StopAsync(harness, payload, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log(payload.Cwd, $"{harness} {hookEvent} failed: {e}");
            return 0;
        }
    }

    private static int PreBash(string harness, HookPayload payload)
    {
        if (payload.Command is not { } command || CommandRewriter.Rewrite(command) is not { } rewritten)
            return 0;
        JsonObject? output = harness switch
        {
            // Claude Code: updatedInput replaces the whole input, so every original field is carried over. No
            // permission decision: the rewritten command goes through the user's normal permission rules.
            "claude" => new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["updatedInput"] = WithCommand(payload.ToolInput, rewritten),
                },
            },
            // Codex honours updatedInput only together with an allow decision.
            "codex" => new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "allow",
                    ["updatedInput"] = new JsonObject { ["command"] = rewritten },
                },
            },
            // Gemini merges tool_input over the model's arguments.
            "gemini" => new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "BeforeTool", ["tool_input"] = new JsonObject { ["command"] = rewritten } },
            },
            // The OpenCode plugin sets the command on the tool's arguments itself.
            "opencode" => new JsonObject { ["command"] = rewritten },
            _ => null,
        };
        if (output is not null)
            Console.Out.Write(output.ToJsonString(Relaxed));
        return 0;
    }

    private static async Task<int> PostEditAsync(string harness, HookPayload payload, CancellationToken cancellationToken)
    {
        var files = payload.EditedFiles().Where(ChangeTracker.IsSource).ToList();
        if (files.Count == 0)
            return 0;
        var root = RepoRoot.Find(Path.GetDirectoryName(files[0])!);
        if (root is null)
            return 0;

        // Claude Code runs this hook in the background (asyncRewake), so it can wait for a cold load. Other harnesses
        // run it inline; there it answers only once the engine is warm and leaves the rest to the Stop hook.
        var background = harness == "claude";
        var (result, response) = await CheckOperation.RunAsync(
            root, files, wait: background, background ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(50), cancellationToken).ConfigureAwait(false);
        if (!ShouldReport(result, response))
            return 0;
        return Report(harness, "PostToolUse", result.Text);
    }

    private static async Task<int> StopAsync(string harness, HookPayload payload, CancellationToken cancellationToken)
    {
        if (payload.StopHookActive)
            return Clean(harness);
        var root = RepoRoot.Find(payload.Cwd);
        if (root is null)
            return Clean(harness);
        var (result, response) = await CheckOperation.RunAsync(root, null, wait: true, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        if (!ShouldReport(result, response))
            return Clean(harness);

        var reason = result.Text + "\nFix these errors before finishing; they are not in the last commit.";
        JsonObject output = harness switch
        {
            "cursor" => new JsonObject { ["followup_message"] = reason },
            "gemini" => new JsonObject { ["decision"] = "deny", ["reason"] = reason },
            _ => new JsonObject { ["decision"] = "block", ["reason"] = reason },
        };
        Console.Out.Write(output.ToJsonString(Relaxed));
        return 0;
    }

    /// <summary>New errors are reported, and so is a missing restore; loading, timeouts and internal failures stay silent.</summary>
    private static bool ShouldReport(OperationResult result, EngineResponse response) =>
        result.Found || response.Error is ErrorCode.RestoreNeeded;

    private static int Report(string harness, string claudeEvent, string text)
    {
        switch (harness)
        {
            case "claude":
                // asyncRewake: exit code 2 wakes the agent and shows stderr as a system reminder.
                Console.Error.Write(text);
                return 2;
            case "cursor":
                Console.Out.Write(new JsonObject { ["additional_context"] = text }.ToJsonString(Relaxed));
                return 0;
            case "copilot":
            case "opencode":
                Console.Out.Write(new JsonObject { ["additionalContext"] = text }.ToJsonString(Relaxed));
                return 0;
            default:
                var eventName = harness == "gemini" ? "AfterTool" : claudeEvent;
                Console.Out.Write(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = eventName, ["additionalContext"] = text } }.ToJsonString(Relaxed));
                return 0;
        }
    }

    private static int Clean(string harness)
    {
        if (harness != "claude")
            Console.Out.Write("{}");
        return 0;
    }

    private static JsonObject WithCommand(JsonElement? input, string command)
    {
        var result = input is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : [];
        result["command"] = command;
        return result;
    }

    private static void Log(string cwd, string message)
    {
        try
        {
            var root = RepoRoot.Find(cwd);
            if (root is null)
                return;
            Directory.CreateDirectory(root.StateDirectory);
            File.AppendAllText(Path.Combine(root.StateDirectory, "hook.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
