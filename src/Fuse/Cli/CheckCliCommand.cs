using Fuse.Operations;
using Fuse.Operations.Check;
using Fuse.Operations.Session;
namespace Fuse.Cli;

/// <summary>The check command and the worker gate built on it.</summary>
internal static class CheckCliCommand
{
    /// <summary>Runs <c>fuse check</c>: errors the working tree has that HEAD does not.</summary>
    internal static async Task<int> CheckAsync(string[] args, CancellationToken cancellationToken)
    {
        var (parsed, refusal) = CheckArguments.Parse(args);
        if (parsed is null)
        {
            Console.Error.WriteLine(refusal);
            Console.Error.WriteLine(CommandUsage.Check);
            return 2;
        }

        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        // A directory or a pattern scopes the check to the changed sources under it; files alone check as named.
        if (parsed.Files.Count > 0 && parsed.Files.Any(IsScopedSpec))
        {
            var (specs, specsRefusal) = CheckOperation.ResolveFiles(parsed.Files, Path.GetFullPath);
            if (specsRefusal is not null)
                return CommandDispatcher.Print(specsRefusal);
            var (scoped, _) = await CheckOperation.RunScopedAsync(root, specs, parsed.MaxErrors, parsed.Json, TimeSpan.FromMinutes(10), cancellationToken, includeTreeStamp: true, session: SessionOperation.EnvironmentSession, brief: parsed.Brief).ConfigureAwait(false);
            return CommandDispatcher.Print(scoped);
        }

        IReadOnlyList<string>? absolute = null;
        if (parsed.Files.Count > 0)
        {
            (absolute, var filesRefusal) = CheckOperation.ResolveFiles(parsed.Files, Path.GetFullPath);
            if (filesRefusal is not null)
                return CommandDispatcher.Print(filesRefusal);
        }

        // FUSE_SESSION names the caller's session in its environment, as a hook names one in its payload: with files it
        // credits them to the session, and without files it answers for that session alone.
        var (result, _) = await CheckOperation.RunAsync(root, absolute, session: SessionOperation.EnvironmentSession, waitForLoad: true, compilationWideAnalyzers: true, TimeSpan.FromMinutes(10), cancellationToken, parsed.MaxErrors, parsed.Json, includeTreeStamp: true, brief: parsed.Brief).ConfigureAwait(false);
        return CommandDispatcher.Print(result);
    }

    /// <summary>Whether <paramref name="spec"/> scopes a check: a directory that exists, or a pattern with a wildcard.</summary>
    internal static bool IsScopedSpec(string spec)
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

    /// <summary>Runs <c>fuse gate</c>: one deterministic verification pass over the check and affected tests.</summary>
    internal static async Task<int> GateAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = CommandDispatcher.RequireRoot();
        if (root is null)
            return 2;
        var result = await GateOperation.RunAsync(root, Environment.CurrentDirectory, args, Console.Out, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == Outcome.Unanswered && result.Text.Contains(GateOperation.Usage, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(result.Text);
            return 2;
        }

        return CommandDispatcher.Print(result);
    }
}
