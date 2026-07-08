using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Fuse.Workspace;

namespace Fuse.Cli.Commands;

/// <summary>
///     Runs the covering tests for a symbol (T1): the tests that reach it through the persisted <c>tests</c> edges,
///     run at build grade (<c>dotnet test</c> scoped by filter to just those test types, the whole suite never
///     run), with per-test verdicts. The CLI counterpart of the <c>fuse_test</c> MCP tool. Reads the persistent
///     index; run <c>fuse index</c> first.
/// </summary>
[CliCommand(
    Name = "test",
    Description = "Run the covering tests for a symbol (the tests that reach it via the persisted tests edges) at build grade, scoped so the whole suite never runs.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class TestCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>Initializes a new instance of the <see cref="TestCommand" /> class for CLI option binding only.</summary>
    public TestCommand() : this(null!)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestCommand" /> class.</summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public TestCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The symbol whose covering tests to run.</summary>
    [CliArgument(Description = "The symbol whose covering tests to run.")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The workspace directory.</summary>
    [CliOption(Required = false, Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The maximum covering test types to run.</summary>
    [CliOption(Required = false, Description = "Maximum covering test types to run.")]
    public int Limit { get; set; } = 20;

    /// <summary>
    ///     Runs the covering tests for the symbol.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    public async Task RunAsync(CliContext context)
    {
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            _consoleUI.WriteError("Specify a symbol whose covering tests to run.");
            return;
        }

        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var covering = await new GraphNeighborhoodExplorer(store).CoveringTestsAsync(Symbol, Limit, context.CancellationToken);
        if (covering.Count == 0)
        {
            _consoleUI.WriteResult($"covering tests for {Symbol}: none (no tests edge reaches it; selection-only floor, nothing to run).");
            return;
        }

        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(root, context.CancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
        {
            _consoleUI.WriteError($"Selected {covering.Count} covering test type(s), but found no solution or project to run them.");
            return;
        }

        var coveringTypes = covering.Select(c => c.Symbol).ToList();
        var filter = TestFilterBuilder.BuildContains(coveringTypes);
        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuse-test", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await BuildGradeTestRunner.RunAsync(target, filter, scratch, TimeSpan.FromMinutes(10), context.CancellationToken);
            _consoleUI.WriteResult(Render(Symbol, coveringTypes, result));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static string Render(string symbol, IReadOnlyList<string> coveringTypes, TestRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("verification grade: build (ran dotnet test scoped to the covering tests; the emit fast path is future work)");
        if (result.TimedOut)
        {
            builder.AppendLine($"covering tests for {symbol}: timed out and the test host was killed.");
            return builder.ToString().TrimEnd();
        }

        if (result.Diagnostics is not null)
        {
            builder.AppendLine($"covering tests for {symbol}: {result.Diagnostics}");
            return builder.ToString().TrimEnd();
        }

        var passed = result.Verdicts.Count(v => v.Outcome == "passed");
        var failed = result.Verdicts.Count(v => v.Outcome == "failed");
        var notRun = result.Verdicts.Count(v => v.Outcome == "not-run");
        builder.AppendLine($"covering tests for {symbol}: {coveringTypes.Count} test type(s), {result.Verdicts.Count} test(s) run - {passed} passed, {failed} failed, {notRun} not-run");
        foreach (var verdict in result.Verdicts.OrderBy(v => v.Outcome == "failed" ? 0 : 1).ThenBy(v => v.Name, StringComparer.Ordinal))
            builder.AppendLine($"  {verdict.Outcome} {verdict.Name}");

        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(coveringTypes, result.Verdicts);
        if (notRunnable.Count > 0)
        {
            builder.AppendLine($"not-runnable ({notRunnable.Count}; selected but produced no result):");
            foreach (var type in notRunnable)
                builder.AppendLine($"  {type}");
        }

        return builder.ToString().TrimEnd();
    }
}
