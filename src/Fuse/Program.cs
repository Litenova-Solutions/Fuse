using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fuse.Engine.Server;
using Fuse.Failures;
using Fuse.Harnesses;
using Fuse.Hooks;
using Fuse.Mcp;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Transport;
namespace Fuse;

internal static class Program
{
    /// <summary>Fuse's own options of <c>fuse test</c>: a near miss of one is refused, and every other token goes to <c>dotnet test</c>.</summary>
    private static readonly string[] TestFuseOptions = ["--all", "--failed", "--clean", "--no-cache", "--max-failures", "--help"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static async Task<int> Main(string[] args)
    {
        // Ultra-fast pre-root exit for read-only PostToolUse hooks (e.g. Muse Code / Claude Code Read/Grep/Glob/LS):
        // peeks tool_name directly from UTF-8 stdin bytes without initializing git roots, harnesses, or cancellations.
        if (args is ["hook", var hookHarness, HookEvent.PostEdit])
            return await FastPostEditHookAsync(hookHarness).ConfigureAwait(false);

        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var command = args.Length > 0 ? args[0] : "help";
        var rest = args.Skip(1).ToArray();
        // Help wins over running: it prints before any root lookup, engine start, build or lock. The hook prints
        // its own usage to stderr, so it is left out here.
        if (command != "hook" && HasHelp(rest))
            return PrintCommandUsage(HelpFor(command), 0);
        try
        {
            return command switch
            {
                "init" => InitCommand.Run(rest),
                "uninit" => await UninitCommand.RunAsync(rest, cancel.Token).ConfigureAwait(false),
                "check" => await CheckAsync(rest, cancel.Token).ConfigureAwait(false),
                "test" => await TestAsync(rest, cancel.Token).ConfigureAwait(false),
                "build" => await BuildAsync(rest, cancel.Token).ConfigureAwait(false),
                "gate" => await GateAsync(rest, cancel.Token).ConfigureAwait(false),
                "judge" => await JudgeAsync(rest, cancel.Token).ConfigureAwait(false),
                "graph" => await GraphAsync(rest, cancel.Token).ConfigureAwait(false),
                "impact" => await ImpactAsync(rest, cancel.Token).ConfigureAwait(false),
                "slim" => await SlimAsync(rest, cancel.Token).ConfigureAwait(false),
                "batch" => await BatchAsync(rest, cancel.Token).ConfigureAwait(false),
                "status" => await StatusAsync(rest, cancel.Token).ConfigureAwait(false),
                "watch" => await WatchAsync(rest, cancel.Token).ConfigureAwait(false),
                "session" => await SessionAsync(rest, cancel.Token).ConfigureAwait(false),
                "stop" => await StopAsync(rest, cancel.Token).ConfigureAwait(false),
                "hook" => await HookCommand.RunAsync(rest, cancel.Token).ConfigureAwait(false),
                "mcp" => await McpAsync(rest, cancel.Token).ConfigureAwait(false),
                "engine" when rest.Length == 1 => await EngineAsync(rest[0]).ConfigureAwait(false),
                "engine" => PrintCommandUsage(CommandUsage.Engine, 2),
                "--version" or "version" => PrintVersion(),
                "help" or "--help" or "-h" => PrintUsage(0),
                _ => PrintUsage(2),
            };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return 130;
        }
    }

    /// <summary>
    ///     Reads stdin once for <c>fuse hook &lt;harness&gt; post-edit</c> and exits 0 immediately when the payload names a
    ///     read-only tool, avoiding <see cref="RepoRoot.Find"/> and harness allocations. When the tool can write files,
    ///     runs the post-edit check directly from the already-read bytes with <c>interactiveFast: true</c>.
    /// </summary>
    private static async Task<int> FastPostEditHookAsync(string harnessName)
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
            await Console.Error.WriteLineAsync($"usage: fuse hook <{string.Join('|', SupportedHarnesses.All.Select(h => h.Name))}> <{HookEvent.PostEdit}|{HookEvent.PreShell}|{HookEvent.Stop}>").ConfigureAwait(false);
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
            var root = RepoRoot.Find(Path.GetDirectoryName(edited[0])!);
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
                files,
                session,
                waitForLoad: background,
                compilationWideAnalyzers: false,
                background ? Remaining() : TimeSpan.FromSeconds(50),
                ct,
                includeNonCSharp: false,
                interactiveFast: true);

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

    /// <summary>Whether <paramref name="rest"/> asks for help: an exact <c>--help</c> or <c>-h</c> before any <c>--</c>, after which every token is an argument.</summary>
    private static bool HasHelp(string[] rest)
    {
        foreach (var token in rest)
        {
            if (token == "--")
                return false;
            if (token is "--help" or "-h")
                return true;
        }

        return false;
    }

    /// <summary>The usage block for <paramref name="command"/>, or the top-level one for a command Fuse does not have.</summary>
    private static string HelpFor(string command) => command switch
    {
        "check" => CommandUsage.Check,
        "test" => CommandUsage.Test,
        "build" => CommandUsage.Build,
        "gate" => CommandUsage.Gate,
        "judge" => CommandUsage.Judge,
        "graph" => CommandUsage.Graph,
        "impact" => CommandUsage.Impact,
        "slim" => CommandUsage.Slim,
        "batch" => CommandUsage.Batch,
        "stop" => CommandUsage.Stop,
        "status" => CommandUsage.Status,
        "watch" => WatchOperation.Usage,
        "session" => CommandUsage.Session,
        "init" => CommandUsage.Init,
        "uninit" => CommandUsage.Uninit,
        "mcp" => CommandUsage.Mcp,
        "engine" => CommandUsage.Engine,
        "--version" or "version" => CommandUsage.Version,
        _ => CommandUsage.Top,
    };

    private static async Task<int> CheckAsync(string[] args, CancellationToken cancellationToken)
    {
        var (parsed, refusal) = CheckArguments.Parse(args);
        if (parsed is null)
        {
            Console.Error.WriteLine(refusal);
            Console.Error.WriteLine(CommandUsage.Check);
            return 2;
        }

        var root = RequireRoot();
        if (root is null)
            return 2;
        // A directory or a pattern scopes the check to the changed sources under it; files alone check as named.
        if (parsed.Files.Count > 0 && parsed.Files.Any(IsScopedSpec))
        {
            var (specs, specsRefusal) = CheckOperation.ResolveFiles(parsed.Files, Path.GetFullPath);
            if (specsRefusal is not null)
                return Print(specsRefusal);
            var (scoped, _) = await CheckOperation.RunScopedAsync(root, specs, parsed.MaxErrors, parsed.Json, TimeSpan.FromMinutes(10), cancellationToken, includeTreeStamp: true, session: SessionOperation.EnvironmentSession, brief: parsed.Brief).ConfigureAwait(false);
            return Print(scoped);
        }

        IReadOnlyList<string>? absolute = null;
        if (parsed.Files.Count > 0)
        {
            (absolute, var filesRefusal) = CheckOperation.ResolveFiles(parsed.Files, Path.GetFullPath);
            if (filesRefusal is not null)
                return Print(filesRefusal);
        }

        // FUSE_SESSION names the caller's session in its environment, as a hook names one in its payload: with files it
        // credits them to the session, and without files it answers for that session alone.
        var (result, _) = await CheckOperation.RunAsync(root, absolute, session: SessionOperation.EnvironmentSession, waitForLoad: true, compilationWideAnalyzers: true, TimeSpan.FromMinutes(10), cancellationToken, parsed.MaxErrors, parsed.Json, includeTreeStamp: true, brief: parsed.Brief).ConfigureAwait(false);
        return Print(result);
    }

    /// <summary>Whether <paramref name="spec"/> scopes a check: a directory that exists, or a pattern with a wildcard.</summary>
    private static bool IsScopedSpec(string spec)
    {
        if (spec.Contains('*') || spec.Contains('?'))
            return true;
        try
        {
            return Directory.Exists(Path.GetFullPath(spec));
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     <c>fuse test</c>. <c>--all</c>, <c>--failed</c> and <c>dotnet test</c> arguments each choose the scope, so two
    ///     together are a usage failure rather than one of them quietly winning. A near miss of a Fuse option is refused;
    ///     every other token goes to <c>dotnet test</c>.
    /// </summary>
    private static async Task<int> TestAsync(string[] args, CancellationToken cancellationToken)
    {
        // After the first -- every token goes to dotnet test unchanged, even one that reads as a Fuse option.
        var dash = Array.IndexOf(args, "--");
        string[] tail = dash < 0 ? [] : args[(dash + 1)..];
        args = dash < 0 ? args : args[..dash];

        // --session is the pre-shell hook's, which names the session whose command it rewrote; it is never passed on.
        string? session = null;
        var index = Array.IndexOf(args, "--session");
        if (index >= 0)
        {
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("fuse: --session takes the session's id");
                return 2;
            }

            session = args[index + 1];
            args = [.. args[..index], .. args[(index + 2)..]];
        }

        // With no --session, FUSE_SESSION in the environment names the session, as it does for fuse check.
        session ??= SessionOperation.EnvironmentSession;

        // --from-rewrite is the pre-shell hook's too: the command ran through Fuse, which the notice says.
        var fromRewrite = args.Contains("--from-rewrite");
        if (fromRewrite)
            args = [.. args.Where(a => a != "--from-rewrite")];

        var clean = false;
        var noCache = false;
        var maxFailures = TestOperation.DefaultMaxFailures;
        var remaining = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--clean")
            {
                clean = true;
                continue;
            }

            if (token == "--no-cache")
            {
                noCache = true;
                continue;
            }

            if (token == "--max-failures" || token.StartsWith("--max-failures=", StringComparison.Ordinal))
            {
                var value = token.StartsWith("--max-failures=", StringComparison.Ordinal)
                    ? token["--max-failures=".Length..]
                    : ++i < args.Length ? args[i] : null;
                if (value is null || !int.TryParse(value, CultureInfo.InvariantCulture, out maxFailures) || maxFailures < 1 || maxFailures > CheckArguments.MaxErrorsLimit)
                {
                    Console.Error.WriteLine($"fuse: --max-failures takes a count of 1 to {CheckArguments.MaxErrorsLimit}, not '{value ?? ""}'");
                    return 2;
                }

                continue;
            }

            remaining.Add(token);
        }

        var strippedArgs = remaining.ToArray();
        var all = strippedArgs.Contains("--all");
        var failed = strippedArgs.Contains("--failed");
        var head = strippedArgs.Where(a => a != "--all" && a != "--failed").ToList();
        var passthrough = head.Concat(tail).ToList();
        if (failed && (all || passthrough.Count > 0))
        {
            Console.Error.WriteLine("fuse: --failed reruns the tests that failed last time and cannot be combined with other arguments; pass the arguments without --failed to choose the scope");
            return 2;
        }

        if (all && passthrough.Count > 0)
        {
            Console.Error.WriteLine("fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope");
            return 2;
        }

        // Only what precedes -- can mistype a Fuse option; past it every token is dotnet test's.
        foreach (var token in head)
        {
            if (NearMiss.Suggest(token, TestFuseOptions) is { } suggestion)
            {
                Console.Error.WriteLine($"fuse: '{token}' is not a fuse test option; did you mean '{suggestion}'?");
                Console.Error.WriteLine(CommandUsage.Test);
                return 2;
            }
        }

        var root = RequireRoot();
        if (root is null)
            return 2;
        if (fromRewrite)
            Console.Error.WriteLine("fuse: dotnet test routed to fuse test (tests affected by your changes; fuse test --all runs every test)");
        var result = await TestOperation.RunAsync(root, Environment.CurrentDirectory, passthrough, all, cancellationToken, session, live: Console.Out, failed: failed, clean: clean, noCache: noCache, maxFailures: maxFailures).ConfigureAwait(false);
        return Print(result);
    }

    private static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var (parsed, refusal) = BuildArguments.Parse(args);
        if (parsed is null)
        {
            Console.Error.WriteLine(refusal);
            Console.Error.WriteLine(CommandUsage.Build);
            return 2;
        }

        if (parsed.FromRewrite)
            Console.Error.WriteLine("fuse: dotnet build routed to fuse build (first 20 errors; fuse build --all-errors for every error)");
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (parsed.Changed)
        {
            // Changed is meaningless without HEAD; a whole build outside a repository still runs.
            if (root is null)
            {
                Console.Error.WriteLine($"fuse: {ErrorMessages.NotARepository}");
                return 2;
            }

            var changed = await BuildOperation.RunChangedAsync(root, Environment.CurrentDirectory, parsed.Passthrough, parsed.ToOptions(), cancellationToken).ConfigureAwait(false);
            return Print(changed);
        }

        var result = await BuildOperation.RunAsync(root, Environment.CurrentDirectory, parsed.Passthrough, parsed.ToOptions(), cancellationToken, includeTreeStamp: true).ConfigureAwait(false);
        return Print(result);
    }

    private static async Task<int> GateAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await GateOperation.RunAsync(root, Environment.CurrentDirectory, args, Console.Out, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered && result.Text.Contains(GateOperation.Usage, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    private static async Task<int> JudgeAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await JudgeOperation.RunAsync(root, Environment.CurrentDirectory, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    private static async Task<int> GraphAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await GraphOperation.RunGraphAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    private static async Task<int> ImpactAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await GraphOperation.RunImpactAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    private static async Task<int> SlimAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await SlimOperation.RunAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    private static async Task<int> BatchAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
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
    private static async Task<int> StatusAsync(string[] args, CancellationToken cancellationToken)
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

        var root = RequireRoot();
        if (root is null)
            return 2;
        return Print(await StatusOperation.RunAsync(root, json, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>fuse watch (--session &lt;name&gt; | --all) [--json]</c>: streams answers and guard violations until cancelled.</summary>
    private static async Task<int> WatchAsync(string[] args, CancellationToken cancellationToken)
    {
        string? session = null;
        var all = false;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && session is null && i + 1 < args.Length)
                session = args[++i];
            else if (args[i].StartsWith("--session=", StringComparison.Ordinal) && session is null && args[i].Length > "--session=".Length)
                session = args[i]["--session=".Length..];
            else if (args[i] == "--all" && !all)
                all = true;
            else if (args[i] == "--json" && !json)
                json = true;
            else
            {
                Console.Error.WriteLine(WatchOperation.Usage);
                return 2;
            }
        }

        if ((all && session is not null) || (!all && session is null))
        {
            Console.Error.WriteLine(WatchOperation.Usage);
            return 2;
        }

        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await WatchOperation.RunAsync(root, session, all, json, Console.Out, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered)
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return result.ExitCode;
    }

    private static async Task<int> SessionAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await SessionOperation.RunAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Text.StartsWith("usage:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return Print(result);
    }

    /// <summary><c>fuse stop</c>, or <c>fuse stop --all</c>; any other argument is a usage failure.</summary>
    private static async Task<int> StopAsync(string[] args, CancellationToken cancellationToken)
    {
        var all = args is ["--all"];
        if (args.Length > 0 && !all)
        {
            Console.Error.WriteLine(CommandUsage.Stop);
            return 2;
        }

        var root = all ? null : RequireRoot();
        if (!all && root is null)
            return 2;
        return Print(await StopOperation.RunAsync(root, all, cancellationToken).ConfigureAwait(false));
    }

    /// <summary><c>fuse mcp</c>, which takes no arguments; any is a usage failure.</summary>
    private static async Task<int> McpAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine(CommandUsage.Mcp);
            return 2;
        }

        return await McpCommand.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RepoRoot? RequireRoot()
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (root is null)
            Console.Error.WriteLine($"fuse: {ErrorMessages.NotARepository}");
        return root;
    }

    private static int Print(OperationResult result)
    {
        Console.Out.WriteLine(result.Text);
        return result.ExitCode;
    }

    /// <summary>
    ///     <c>fuse engine &lt;root&gt;</c>. The native client cannot load MSBuild or analyzers, so the engine runs only from
    ///     <see cref="EngineVersion.ManagedAssembly"/>. The check reads <see cref="RuntimeFeature.IsDynamicCodeSupported"/>
    ///     itself, not <see cref="EngineVersion.IsNativeClient"/>, because the native compiler treats that property as a
    ///     constant only where it is read directly, and then leaves the engine, Roslyn and MSBuild out of the native client.
    /// </summary>
    private static async Task<int> EngineAsync(string root)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            await Console.Error.WriteLineAsync($"fuse: the engine runs from {EngineVersion.ManagedAssembly} on the .NET runtime, not from the native client; the client starts it").ConfigureAwait(false);
            return 2;
        }

        return await EngineServer.RunAsync(root).ConfigureAwait(false);
    }

    private static int PrintVersion()
    {
        Console.Out.WriteLine(EngineVersion.Product);
        return 0;
    }

    private static int PrintUsage(int exitCode) => PrintCommandUsage(CommandUsage.Top, exitCode);

    private static int PrintCommandUsage(string usage, int exitCode)
    {
        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(usage);
        return exitCode;
    }
}
