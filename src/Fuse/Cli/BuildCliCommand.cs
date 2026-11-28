using Fuse.Failures;
using Fuse.Operations;
using Fuse.Operations.Build;
using Fuse.Paths;
namespace Fuse.Cli;

/// <summary>The build command, which runs dotnet build and prints its errors.</summary>
internal static class BuildCliCommand
{
    /// <summary>Runs <c>fuse build</c>: dotnet build over the working tree, or over the changed projects with <c>--changed</c>.</summary>
    internal static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = BuildArguments.Parse(args);
        if (parsed is ParseResult<BuildArguments>.Refused refused)
        {
            Console.Error.WriteLine(refused.Message);
            Console.Error.WriteLine(CommandUsage.Build);
            return 2;
        }

        var arguments = ((ParseResult<BuildArguments>.Parsed)parsed).Value;
        if (arguments.FromRewrite)
            Console.Error.WriteLine($"fuse: dotnet build routed to fuse build (first 20 errors; {BuildOperation.AllErrorsHint(arguments.Passthrough, arguments.Changed)} for every error)");
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (arguments.Changed)
        {
            // Changed is meaningless without HEAD; a whole build outside a repository still runs.
            if (root is null)
            {
                Console.Error.WriteLine($"fuse: {ErrorMessages.NotARepository}");
                return 2;
            }

            var changed = await BuildOperation.RunChangedAsync(root, Environment.CurrentDirectory, arguments.Passthrough, arguments.ToOptions(), cancellationToken).ConfigureAwait(false);
            return CommandDispatcher.Print(changed);
        }

        var result = await BuildOperation.RunAsync(root, Environment.CurrentDirectory, arguments.Passthrough, arguments.ToOptions(), cancellationToken, includeTreeStamp: true).ConfigureAwait(false);
        return CommandDispatcher.Print(result);
    }
}
