using System.Text;
using System.Text.Json;
using Fuse.Identity;
using Fuse.Harnesses;
using Fuse.Harnesses.Adapters;
using Fuse.Hooks;
using Fuse.Mcp;
using Fuse.Operations;
using Fuse.Operations.Check;
using Fuse.Operations.Control;
using Fuse.Operations.Inspection;
using Fuse.Operations.Judge;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
namespace Fuse.Cli;

/// <summary>The remaining client commands: inspection, control, the hook fast path, and the MCP server.</summary>
internal static class ControlCliCommand
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    ///     Reads stdin once for <c>fuse hook &lt;harness&gt; post-edit</c> and exits 0 immediately when the payload names a
    ///     read-only tool, avoiding <see cref="RepoRoot.Find"/> and harness allocations. When the tool can write files,
    ///     runs the post-edit check directly from the already-read bytes with <c>interactiveFast: true</c>.
    /// </summary>
    internal static async Task<int> FastPostEditHookAsync(string harnessName)
    {
        byte[] stdinBytes;
        try
        {
            await using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            await stdin.CopyToAsync(buffer).ConfigureAwait(false);
            stdinBytes = buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (IsReadOnlyPostEditPayload(harnessName, stdinBytes))
            return 0;

        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var harness = SupportedHarnesses.Find(harnessName);
        if (harness is null)
        {
            await Console.Error.WriteLineAsync($"usage: fuse hook <{string.Join('|', SupportedHarnesses.All.Select(h => h.Name))} <{HookEvent.PostEdit}|{HookEvent.PreShell}|{HookEvent.Stop}>").ConfigureAwait(false);
            return 0;
        }

        HookPayload payload;
        try
        {
            var text = Utf8NoBom.GetString(stdinBytes);
            payload = HookPayload.Parse(text);
        }
        catch (JsonException e)
        {
            LogHook(Environment.CurrentDirectory, $"{harness.Name} {HookEvent.PostEdit} payload was not valid JSON: {e.Message}");
            return 0;
        }

        if (harness.IsAlsoRunByCursor && payload.FromCursor)
            return 0;

        try
        {
            if (!harness.WritesFiles(payload.ToolName))
                return 0;
            var edited = payload.EditedFiles().Where(PathRules.IsSource).ToList();
            if (edited.Count == 0)
                return 0;
            var directory = Path.GetDirectoryName(edited[0]);
            if (directory is null)
                return 0;
            var root = RepoRoot.Find(directory);
            if (root is null)
                return 0;

            var files = edited.Select(root.PathOf).Distinct().Select(p => p.Absolute).ToList();
            var background = harness.RunsPostEditInBackground;
            var session = payload.Session is { } id ? $"{harness.Name}:{id}" : null;
            if (session is not null)
            {
                SessionRoots.Record(session, root);
                BlockedStops.NoteWrittenOnce(root, session, files.Select(f => Path.GetRelativePath(root.Path, f).Replace('\\', '/')));
            }

            var started = Environment.TickCount64;
            TimeSpan Remaining() => HookCommand.LongWait - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            Task<(OperationResult Result, EngineResponse Response)> Check(CancellationToken ct) => CheckOperation.RunAsync(
                root,
                new CheckInvocation(
                    Files: files,
                    Session: SessionId.TryParse(session, out var fastSession) ? fastSession : null,
                    WaitForLoad: background,
                    CompilationWideAnalyzers: false,
                    Limit: ErrorLimit.Default,
                    Json: false,
                    IncludeTreeStamp: false,
                    IncludeNonCSharp: false,
                    Brief: false,
                    InteractiveFast: true),
                background ? Remaining() : TimeSpan.FromSeconds(50),
                ct);

            var answer = await HookCommand.PostEditAsync(harness, Check, Remaining, message => LogHook(root.Path, message), cancel.Token).ConfigureAwait(false);
            if (answer.StandardOutput.Length > 0)
                Console.Out.Write(answer.StandardOutput);
            if (answer.StandardError.Length > 0)
                Console.Error.Write(answer.StandardError);
            return answer.ExitCode;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHook(payload.Cwd, $"{harness.Name} {HookEvent.PostEdit} failed: {e}");
            return 0;
        }
    }

    /// <summary>
    ///     Zero-DOM <see cref="Utf8JsonReader"/> pre-root check: returns true when <paramref name="utf8Json"/> is a valid
    ///     JSON object whose top-level <c>tool_name</c> / <c>toolName</c> is a known read-only tool (or, for
    ///     <c>muse</c>, any tool other than <c>edit_file</c>, <c>write_file</c>, or <c>apply_patch</c>). Malformed JSON
    ///     returns false so the full parser logs it to <c>hook.log</c>.
    /// </summary>
    internal static bool IsReadOnlyPostEditPayload(string harnessName, ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
            return false;
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            string? toolName = null;
            var cursor = false;
            var depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    depth++;
                    continue;
                }

                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    if (depth == 0)
                        break;
                    depth--;
                    continue;
                }

                if (depth != 0 || reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("tool_name") || reader.ValueTextEquals("toolName"))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                        toolName = reader.GetString();
                }
                else if (reader.ValueTextEquals("cursor_version"))
                {
                    cursor = true;
                }
            }

            if (cursor || string.IsNullOrWhiteSpace(toolName))
                return false;
            if (harnessName == "muse")
                return toolName is not ("edit_file" or "write_file" or "apply_patch");
            return MuseCode.IsReadOnlyTool(toolName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void LogHook(string cwd, string message)
    {
        try
        {
            var root = RepoRoot.Find(cwd);
            if (root is null)
                return;
            LocalState.RecordRoot(root);
            File.AppendAllText(Path.Combine(root.StateDirectory, "hook.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }
    }

    /// <summary>Runs <c>fuse judge</c>: evaluates candidate patches or worktrees without writing them.</summary>
    internal static async Task<int> JudgeAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await JudgeOperation.RunAsync(root, Environment.CurrentDirectory, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }

    /// <summary>Runs <c>fuse graph</c>: prints the project DAG and test ownership map.</summary>
    internal static async Task<int> GraphAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await GraphOperation.RunGraphAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }

    /// <summary>Runs <c>fuse impact</c>: prints the projects and tests the named paths reach.</summary>
    internal static async Task<int> ImpactAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await ImpactOperation.RunImpactAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }

    /// <summary>Runs <c>fuse slim</c>: asks the engine to shed caches and compact memory.</summary>
    internal static async Task<int> SlimAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await SlimOperation.RunAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }

    /// <summary>Runs <c>fuse batch</c>: processes NDJSON engine requests over one persistent pipe.</summary>
    internal static async Task<int> BatchAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        Console.InputEncoding = new UTF8Encoding(false);
        var result = await BatchOperation.RunAsync(root, args, Console.In, Console.Out, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return result.ExitCode;
    }

    /// <summary><c>fuse status</c>: the engine and the changed files, or the usage failure for an argument it does not take.</summary>
    internal static async Task<int> StatusAsync(string[] args, CancellationToken cancellationToken)
    {
        var json = false;
        foreach (var token in args)
        {
            if (token == "--json")
            {
                json = true;
                continue;
            }

            Console.Error.WriteLine($"fuse: status takes [--json]; '{token}' is not one");
            Console.Error.WriteLine(CommandUsage.Status);
            return 2;
        }

        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        return CommandDispatcher.Print(await StatusOperation.RunAsync(root, json, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>fuse stop</c>, or <c>fuse stop --all</c>; any other argument is a usage failure.</summary>
    internal static async Task<int> StopAsync(string[] args, CancellationToken cancellationToken)
    {
        var all = args is ["--all"];
        if (args.Length > 0 && !all)
        {
            Console.Error.WriteLine(CommandUsage.Stop);
            return 2;
        }

        var root = all ? null : CommandDispatcher.RequireRoot();
        if (!all && root is null)
            return 2;
        return CommandDispatcher.Print(await StopOperation.RunAsync(root, all, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>fuse mcp</c>, which takes no arguments; any is a usage failure.</summary>
    internal static async Task<int> McpAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine(CommandUsage.Mcp);
            return 2;
        }

        return await McpCommand.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
