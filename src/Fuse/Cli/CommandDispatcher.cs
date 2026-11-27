using Fuse.Harnesses.Setup;
using Fuse.Hooks;
using Fuse.Operations;
using Fuse.Operations.Session;
using Fuse.Paths;
using Fuse.Failures;
namespace Fuse.Cli;

/// <summary>The client command the arguments name, and the shared console output it writes through.</summary>
internal static class CommandDispatcher
{
    /// <summary>Runs every client command the arguments name, after the fast hook path and the help check.</summary>
    internal static async Task<int> DispatchAsync(string[] args)
    {
        if (args is ["hook", var harnessName, "post-edit"])
            return await ControlCliCommand.FastPostEditHookAsync(harnessName).ConfigureAwait(false);

        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
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
                "check" => await CheckCliCommand.CheckAsync(rest, cancel.Token).ConfigureAwait(false),
                "test" => await TestCliCommand.TestAsync(rest, cancel.Token).ConfigureAwait(false),
                "build" => await BuildCliCommand.BuildAsync(rest, cancel.Token).ConfigureAwait(false),
                "gate" => await CheckCliCommand.GateAsync(rest, cancel.Token).ConfigureAwait(false),
                "judge" => await ControlCliCommand.JudgeAsync(rest, cancel.Token).ConfigureAwait(false),
                "graph" => await ControlCliCommand.GraphAsync(rest, cancel.Token).ConfigureAwait(false),
                "impact" => await ControlCliCommand.ImpactAsync(rest, cancel.Token).ConfigureAwait(false),
                "slim" => await ControlCliCommand.SlimAsync(rest, cancel.Token).ConfigureAwait(false),
                "batch" => await ControlCliCommand.BatchAsync(rest, cancel.Token).ConfigureAwait(false),
                "status" => await ControlCliCommand.StatusAsync(rest, cancel.Token).ConfigureAwait(false),
                "watch" => await SessionCliCommand.WatchAsync(rest, cancel.Token).ConfigureAwait(false),
                "session" => await SessionCliCommand.SessionAsync(rest, cancel.Token).ConfigureAwait(false),
                "stop" => await ControlCliCommand.StopAsync(rest, cancel.Token).ConfigureAwait(false),
                "hook" => await HookCommand.RunAsync(rest, cancel.Token).ConfigureAwait(false),
                "mcp" => await ControlCliCommand.McpAsync(rest, cancel.Token).ConfigureAwait(false),
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
    internal static bool HasHelp(string[] rest)
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
    internal static string HelpFor(string command) => command switch
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

    /// <summary>Finds the repository holding the current directory, or writes the refusal and returns null.</summary>
    internal static RepoRoot? RequireRoot()
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (root is null)
            Console.Error.WriteLine($"fuse: {ErrorMessages.NotARepository}");
        return root;
    }

    /// <summary>Writes <paramref name="result"/> to standard output and returns its exit code.</summary>
    internal static int Print(OperationResult result)
    {
        Console.Out.WriteLine(result.Text);
        return result.ExitCode;
    }

    /// <summary>Prints the product version to standard output.</summary>
    internal static int PrintVersion()
    {
        Console.Out.WriteLine(Fuse.Protocol.Transport.EngineVersion.Product);
        return 0;
    }

    /// <summary>Prints the top-level usage and returns <paramref name="exitCode"/>.</summary>
    internal static int PrintUsage(int exitCode) => PrintCommandUsage(CommandUsage.Top, exitCode);

    /// <summary>Writes <paramref name="usage"/> to standard output for exit 0 and standard error otherwise.</summary>
    internal static int PrintCommandUsage(string usage, int exitCode)
    {
        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(usage);
        return exitCode;
    }
}
