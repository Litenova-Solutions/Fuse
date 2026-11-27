using Fuse.Failures;
using Fuse.Operations.Build;
using Fuse.Paths;
namespace Fuse.Cli;

/// <summary>The build command, which runs dotnet build and prints its errors.</summary>
internal static class BuildCliCommand
{
    /// <summary>Runs <c>fuse build</c>: dotnet build over the working tree, or over the changed projects with <c>--changed</c>.</summary>
    internal static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
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
            return CommandDispatcher.Print(changed);
        }

        var result = await BuildOperation.RunAsync(root, Environment.CurrentDirectory, parsed.Passthrough, parsed.ToOptions(), cancellationToken, includeTreeStamp: true).ConfigureAwait(false);
        return CommandDispatcher.Print(result);
    }
}
