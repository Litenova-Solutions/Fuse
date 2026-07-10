using DotMake.CommandLine;
using Fuse.Benchmarks;
using Fuse.Cli.Services;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Retrieval;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Runs Fuse's evaluation suites. A thin entry point that delegates to the <c>Fuse.Benchmarks</c>
///     library, which owns the typed suites: <c>semantics</c> (Suite A, semantic graph vs edge gold),
///     <c>review</c> (Suite B, change-impact recall/precision over PR ground truth), <c>localize</c>
///     (Suite C, open-ended localization by signal bucket), <c>ranking</c> (the ranking regression suite:
///     MRR, recall@k, nDCG@k for the lexical channel and the shipping default), <c>checkgate</c> (Suite F,
///     the <c>fuse_check</c> false-green and false-red rates over known-good and known-bad edits),
///     <c>loop</c> (Suite R4, the build-gated turns to green per toolbox), and <c>agent</c> (Suite D, agent
///     context sufficiency via the Claude Code CLI).
/// </summary>
/// <remarks>
///     Corpus-bound suites (review/localize/agent) skip gracefully when the pinned corpus is absent, so
///     a bare invocation stays offline. Results are written to <c>tests/benchmarks/results/&lt;suite&gt;.json</c>
///     (or <c>--output</c>) as the single source of truth for quoted numbers.
/// </remarks>
[CliCommand(
    Name = "eval",
    Description = "Run Fuse evaluation suites (semantics, review, localize, ranking, checkgate, diagbench, loop, agent, reduce, performance).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class EvalCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IChangeSource _changeSource;
    private readonly FusionOrchestrator _orchestrator;
    private readonly ProjectTemplateRegistry _templateRegistry;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EvalCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public EvalCommand() : this(null!, null!, null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="EvalCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="changeSource">The git change source for corpus-bound suites.</param>
    /// <param name="orchestrator">The fusion orchestrator used by the reduction suite.</param>
    /// <param name="templateRegistry">The template registry used by the reduction suite.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public EvalCommand(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _changeSource = changeSource;
        _orchestrator = orchestrator;
        _templateRegistry = templateRegistry;
        _consoleUI = consoleUI;
    }

    /// <summary>The suite to run: <c>semantics</c>, <c>review</c>, <c>localize</c>, <c>ranking</c>, or <c>agent</c>.</summary>
    [CliArgument(Description = "The suite to run: semantics, review, localize, ranking, checkgate, diagbench, loop, agent, reduce, performance.")]
    public string Suite { get; set; } = "semantics";

    /// <summary>The benchmark root holding corpus.json, prs.json, and results. Defaults to tests/benchmarks under the current directory.</summary>
    [CliOption(Required = false, Description = "Benchmark root (holds corpus.json, prs.json, results).")]
    public string? BenchRoot { get; set; }

    /// <summary>The fixtures root for the semantics suite (a directory of fixtures, or a single fixture directory).</summary>
    [CliOption(Required = false, Description = "Fixtures root for the semantics suite.")]
    public string? Fixtures { get; set; }

    /// <summary>The corpus root holding the checked-out repositories. Defaults to .corpus under the benchmark root.</summary>
    [CliOption(Required = false, Description = "Corpus root (checked-out repositories).")]
    public string? Corpus { get; set; }

    /// <summary>A per-repo task cap (0 means all).</summary>
    [CliOption(Required = false, Description = "Per-repo task cap (0 = all).")]
    public int Limit { get; set; }

    /// <summary>Restrict the run to a single repository by name.</summary>
    [CliOption(Required = false, Description = "Restrict to a single repository by name.")]
    public string? Repo { get; set; }

    /// <summary>Comma-separated token budgets for the review suite (for example 25000,50000).</summary>
    [CliOption(Required = false, Description = "Comma-separated token budgets (review).")]
    public string? Budgets { get; set; }

    /// <summary>The model id for the agent suite.</summary>
    [CliOption(Required = false, Description = "Model id for the agent suite.")]
    public string? Model { get; set; }

    /// <summary>An alternate corpus manifest path (C4 corpus v2), or null for corpus.json.</summary>
    [CliOption(Name = "--manifest", Required = false, Description = "Alternate corpus manifest path (corpus v2); defaults to corpus.json under the benchmark root.")]
    public string? Manifest { get; set; }

    /// <summary>The number of agent rollouts per task.</summary>
    [CliOption(Required = false, Description = "Agent rollouts per task.")]
    public int Rollouts { get; set; } = 1;

    /// <summary>When set, run dotnet restore on each checkout before indexing so it can load semantically.</summary>
    [CliOption(Required = false, Description = "Run dotnet restore on each checkout before indexing (semantic mode).")]
    public bool Restore { get; set; }

    /// <summary>When set, skip (do not score) any checkout that indexes below semantic mode, reporting it loudly.</summary>
    [CliOption(Required = false, Description = "Skip checkouts that index below semantic mode instead of scoring the fallback.")]
    public bool RequireSemantic { get; set; }

    /// <summary>When greater than zero, the semantics suite samples this many predicted edges per type over the corpus for adjudication.</summary>
    [CliOption(Required = false, Description = "Sample N predicted edges per type over the corpus (semantics adjudication).")]
    public int CorpusSample { get; set; }

    /// <summary>When greater than zero, the checkgate suite runs this many compiler-verified mutants per class per fixture (the scaled honesty gate).</summary>
    [CliOption(Required = false, Description = "Run N compiler-verified mutants per class per fixture through checkgate (the scaled honesty gate).")]
    public int Mutations { get; set; }

    /// <summary>When greater than zero and a build-capture worker is configured, checkgate runs this many mutants through both the oracle and build-grade paths and records their diagnostic-identity agreement (the T0 verify-agreement gate).</summary>
    [CliOption(Required = false, Description = "Run N mutants through both the oracle and build-grade paths and record their diagnostic-identity agreement (the T0 verify-agreement gate; needs FUSE_BUILD_CAPTURE_WORKER).")]
    public int VerifyAgreement { get; set; }

    /// <summary>An optional path to write the JSON results to. Defaults to results/&lt;suite&gt;.json under the benchmark root.</summary>
    [CliOption(Required = false, Description = "Path to write JSON results to.")]
    public string? Output { get; set; }

    /// <summary>
    ///     Runs the eval command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the suite has run.</returns>
    public async Task RunAsync(CliContext context)
    {
        var benchRoot = Path.GetFullPath(BenchRoot ?? Path.Combine("tests", "benchmarks"));
        var options = new EvalOptions(
            benchRoot,
            CorpusRoot: Corpus is null ? null : Path.GetFullPath(Corpus),
            FixturesRoot: Fixtures is null ? null : Path.GetFullPath(Fixtures),
            Budgets: ParseBudgets(Budgets),
            Limit: Limit,
            RepoFilter: Repo,
            AgentModel: Model,
            Rollouts: Rollouts,
            Restore: Restore,
            RequireSemantic: RequireSemantic,
            CorpusSample: CorpusSample,
            Mutations: Mutations,
            VerifyAgreement: VerifyAgreement,
            ManifestPath: Manifest is null ? null : Path.GetFullPath(Manifest),
            Log: _consoleUI.WriteStep);

        var suite = BuildSuite(Suite.Trim().ToLowerInvariant());
        if (suite is null)
        {
            _consoleUI.WriteError($"Unknown suite '{Suite}'. Supported: semantics, review, localize, ranking, checkgate, diagbench, loop, agent, reduce, performance.");
            return;
        }

        var result = await suite.RunAsync(options, context.CancellationToken);
        _consoleUI.WriteResult(Reporting.FormatScorecard(result));

        // The corpus-health suite writes its own machine-readable report (corpus-health.json) inside RunAsync;
        // skip the generic SuiteResult write so it is not clobbered. All other suites write their SuiteResult.
        if (suite.Name == "corpus-health" && Output is null)
        {
            _consoleUI.WriteStep($"Wrote results to {Path.Combine(options.ResultsRoot, CorpusHealthReport.FileName)}");
            return;
        }

        var outputPath = Output is null
            ? Path.Combine(options.ResultsRoot, $"{suite.Name}.json")
            : Path.GetFullPath(Output);
        await Reporting.WriteAsync(result, outputPath, context.CancellationToken);
        _consoleUI.WriteStep($"Wrote results to {outputPath}");
    }

    private IEvalSuite? BuildSuite(string name) => name switch
    {
        "semantics" => new SemanticResolutionSuite(_indexer),
        "review" => new ChangeImpactSuite(_indexer, _changeSource),
        "localize" => new LocalizationSuite(_indexer, _changeSource),
        "ranking" => new RankingSuite(_indexer, _changeSource),
        "checkgate" => new CheckGateSuite(),
        "diagbench" => new DiagBenchSuite(),
        "loop" => new LoopSuite(_indexer),
        "agent" => new AgentSuite(_indexer),
        "corpus-health" => new CorpusHealthSuite(_indexer),
        "performance" => new PerformanceSuite(_indexer, _changeSource),
        "reduce" => new ReductionSuite((dir, files, level, ct) =>
            ReduceRunner.ReduceFilesAsync(_orchestrator, _templateRegistry, dir, files, ParseLevel(level), null, ct)),
        _ => null
    };

    private static ReductionLevel ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "none" => ReductionLevel.None,
        "aggressive" => ReductionLevel.Aggressive,
        "skeleton" => ReductionLevel.Skeleton,
        "publicapi" => ReductionLevel.PublicApi,
        _ => ReductionLevel.Standard
    };

    private static IReadOnlyList<int>? ParseBudgets(string? budgets)
    {
        if (string.IsNullOrWhiteSpace(budgets))
            return null;
        return budgets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => int.TryParse(b, out var v) ? v : 0)
            .Where(v => v > 0)
            .ToList();
    }
}
