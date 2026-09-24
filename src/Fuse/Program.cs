using System.Text;
using Fuse.Cli;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Mcp;
using Fuse.Repo;

namespace Fuse;

internal static class Program
{
    private const string Usage = """
        fuse - instant C# compiler feedback and affected-test runs for coding agents

          fuse init                 register Fuse's hooks with the agent harnesses this repository uses
          fuse check [files...]     errors the working tree has that HEAD did not, across dependent projects
          fuse test [args...]       run the tests affected by your changes (with args: exactly what dotnet test runs)
          fuse test --all           run every test
          fuse build [args...]      dotnet build, printing only errors
          fuse mcp                  stdio MCP server (fuse_check, fuse_test, fuse_build) for hosts without hooks
          fuse hook <harness> <event>   entry point for installed hooks
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
        var absolute = files.Length == 0 ? null : files.Select(Path.GetFullPath).ToList();
        var (result, _) = await CheckOperation.RunAsync(root, absolute, wait: true, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        return Print(result);
    }

    private static async Task<int> TestAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RequireRoot();
        if (root is null)
            return 2;
        var all = args.Contains("--all");
        var passthrough = args.Where(a => a != "--all").ToList();
        var result = await TestOperation.RunAsync(root, Environment.CurrentDirectory, passthrough, all, cancellationToken).ConfigureAwait(false);
        return Print(result);
    }

    private static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        var result = await BuildOperation.RunAsync(Environment.CurrentDirectory, root?.Path ?? Environment.CurrentDirectory, args, cancellationToken).ConfigureAwait(false);
        return Print(result);
    }

    private static RepoRoot? RequireRoot()
    {
        var root = RepoRoot.Find(Environment.CurrentDirectory);
        if (root is null)
            Console.Error.WriteLine("fuse: not inside a git repository; Fuse compares your changes with HEAD, so it needs one");
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
