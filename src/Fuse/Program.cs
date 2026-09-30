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
    private const string Usage = """
        fuse - faster .NET build and test loop for AI coding agents

          fuse init                 register Fuse's hooks with the agent harnesses this repository uses
          fuse check [files...]     errors the working tree has that HEAD does not, across dependent projects
          fuse test [args...]       run the tests affected by your changes (with args: the tests those dotnet test arguments name)
          fuse test --all           run every test
          fuse build [args...]      dotnet build, printing its errors
          fuse mcp                  stdio MCP server (fuse_check, fuse_test, fuse_build) for MCP hosts that run no hooks
          fuse hook <harness> <event>   the command registered hooks run
        """;

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
        try
        {
            return command switch
            {
                "init" => InitCommand.Run(),
                "check" => await CheckAsync(rest, cancel.Token).ConfigureAwait(false),
                "test" => await TestAsync(rest, cancel.Token).ConfigureAwait(false),
                "build" => await BuildAsync(rest, cancel.Token).ConfigureAwait(false),
                "hook" => await HookCommand.RunAsync(rest, cancel.Token).ConfigureAwait(false),
                "mcp" => await McpCommand.RunAsync(cancel.Token).ConfigureAwait(false),
                "engine" when rest.Length == 1 => await EngineServer.RunAsync(rest[0]).ConfigureAwait(false),
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

    private static async Task<int> CheckAsync(string[] files, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        IReadOnlyList<string>? absolute = null;
        if (files.Length > 0)
        {
            (absolute, var refusal) = CheckOperation.ResolveFiles(files, Path.GetFullPath);
            if (refusal is not null)
                return Print(refusal);
        }

        var (result, _) = await CheckOperation.RunAsync(root, absolute, waitForLoad: true, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        return Print(result);
    }

    /// <summary>
    ///     <c>fuse test</c>. <c>--all</c> and <c>dotnet test</c> arguments each choose the scope, so the two together are a
    ///     usage failure rather than one of them quietly winning.
    /// </summary>
    private static async Task<int> TestAsync(string[] args, CancellationToken cancellationToken)
    {
        var all = args.Contains("--all");
        var passthrough = args.Where(a => a != "--all").ToList();
        if (all && passthrough.Count > 0)
        {
            Console.Error.WriteLine("fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope");
            return 2;
        }

        var root = RequireRoot();
        if (root is null)
            return 2;
        var result = await TestOperation.RunAsync(root, Environment.CurrentDirectory, passthrough, all, cancellationToken).ConfigureAwait(false);
        return Print(result);
    }

    private static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        var result = await BuildOperation.RunAsync(root, Environment.CurrentDirectory, args, cancellationToken).ConfigureAwait(false);
        return Print(result);
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

    private static int PrintVersion()
    {
        Console.Out.WriteLine(EngineVersion.Product);
        return 0;
    }

    private static int PrintUsage(int exitCode)
    {
        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(Usage);
        return exitCode;
    }
}
