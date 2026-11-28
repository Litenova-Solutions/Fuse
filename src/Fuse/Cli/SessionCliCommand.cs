using Fuse.Operations;
using Fuse.Operations.Session;
namespace Fuse.Cli;

/// <summary>The session commands: registration, contract guard, and the watch stream.</summary>
internal static class SessionCliCommand
{
    /// <summary>Runs <c>fuse session</c>: registers an external writer, or freezes contract patterns for a guard.</summary>
    internal static async Task<int> SessionAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await SessionOperation.RunAsync(root, args, cancellationToken).ConfigureAwait(false);
        if (result.Text.StartsWith("usage:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }

    /// <summary><c>fuse watch (--session &lt;name&gt; | --all) [--json]</c>: streams answers and guard violations until cancelled.</summary>
    internal static async Task<int> WatchAsync(string[] args, CancellationToken cancellationToken)
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

        var root = CommandDispatcher.RequireRoot();
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
}
