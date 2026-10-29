using System.Runtime.CompilerServices;
using System.Text;
using Fuse.Engine;
using Fuse.Failures;
using Fuse.Harnesses;
using Fuse.Hooks;
using Fuse.Mcp;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse;

internal static class Program
{
    /// <summary>Fuse's own options of <c>fuse test</c>: a near miss of one is refused, and every other token goes to <c>dotnet test</c>.</summary>
    private static readonly string[] TestFuseOptions = ["--all", "--failed", "--help"];

    public static async Task<int> Main(string[] args)
    {
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
                "status" => await StatusAsync(rest, cancel.Token).ConfigureAwait(false),
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
        "stop" => CommandUsage.Stop,
        "status" => CommandUsage.Status,
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
            var (scoped, _) = await CheckOperation.RunScopedAsync(root, specs, parsed.MaxErrors, parsed.Json, TimeSpan.FromMinutes(10), cancellationToken, includeTreeStamp: true).ConfigureAwait(false);
            return Print(scoped);
        }

        IReadOnlyList<string>? absolute = null;
        if (parsed.Files.Count > 0)
        {
            (absolute, var filesRefusal) = CheckOperation.ResolveFiles(parsed.Files, Path.GetFullPath);
            if (filesRefusal is not null)
                return Print(filesRefusal);
        }

        var (result, _) = await CheckOperation.RunAsync(root, absolute, session: null, waitForLoad: true, compilationWideAnalyzers: true, TimeSpan.FromMinutes(10), cancellationToken, parsed.MaxErrors, parsed.Json, includeTreeStamp: true).ConfigureAwait(false);
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

        // --from-rewrite is the pre-shell hook's too: the command ran through Fuse, which the notice says.
        var fromRewrite = args.Contains("--from-rewrite");
        if (fromRewrite)
            args = [.. args.Where(a => a != "--from-rewrite")];

        var all = args.Contains("--all");
        var failed = args.Contains("--failed");
        var head = args.Where(a => a != "--all" && a != "--failed").ToList();
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
        var result = await TestOperation.RunAsync(root, Environment.CurrentDirectory, passthrough, all, cancellationToken, session, live: Console.Out, failed: failed).ConfigureAwait(false);
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
