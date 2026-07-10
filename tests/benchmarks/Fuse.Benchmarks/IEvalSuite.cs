namespace Fuse.Benchmarks;

/// <summary>
///     Options controlling an evaluation run, shared across suites. A suite uses the fields relevant to
///     it and ignores the rest.
/// </summary>
/// <param name="BenchRoot">The benchmark root directory (holds <c>corpus.json</c>, <c>prs.json</c>, <c>results</c>).</param>
/// <param name="CorpusRoot">The directory holding the checked-out corpus repositories, or null to use the default under <see cref="BenchRoot" />.</param>
/// <param name="FixturesRoot">The fixtures root for the semantics suite, or null.</param>
/// <param name="Budgets">Token budgets to evaluate at, or null for the suite default.</param>
/// <param name="Limit">A per-repo task cap (0 means all).</param>
/// <param name="RepoFilter">A single repository name to restrict the run to, or null for all.</param>
/// <param name="AgentModel">The model id for the agent suite, or null for its default.</param>
/// <param name="Rollouts">The number of agent rollouts per task.</param>
/// <param name="Restore">When true, run <c>dotnet restore</c> on each checkout before indexing so it can load semantically.</param>
/// <param name="RequireSemantic">When true, do not score a task whose checkout indexes below semantic mode; report it loudly instead of silently scoring the syntax fallback.</param>
/// <param name="CorpusSample">When greater than zero, the semantics suite samples this many predicted edges per type over the corpus for adjudication.</param>
/// <param name="Mutations">When greater than zero, the checkgate suite runs this many compiler-verified mutants per class per fixture (the scaled honesty gate, H1).</param>
/// <param name="VerifyAgreement">When greater than zero and a build-capture worker is configured, the checkgate suite runs this many mutants through both the oracle path and the build-grade path and records their diagnostic-identity agreement (the T0 verify-agreement gate).</param>
/// <param name="ManifestPath">An alternate corpus manifest path (C4 corpus v2), or null to use <c>corpus.json</c> under the benchmark root.</param>
/// <param name="Log">A progress callback, or null for no output.</param>
public sealed record EvalOptions(
    string BenchRoot,
    string? CorpusRoot = null,
    string? FixturesRoot = null,
    IReadOnlyList<int>? Budgets = null,
    int Limit = 0,
    string? RepoFilter = null,
    string? AgentModel = null,
    int Rollouts = 1,
    bool Restore = false,
    bool RequireSemantic = false,
    int CorpusSample = 0,
    int Mutations = 0,
    int VerifyAgreement = 0,
    string? ManifestPath = null,
    Action<string>? Log = null)
{
    /// <summary>Writes a progress line through <see cref="Log" />, if one is set.</summary>
    /// <param name="message">The message.</param>
    public void Report(string message) => Log?.Invoke(message);

    /// <summary>The resolved corpus directory: <see cref="CorpusRoot" /> if set, else <c>.corpus</c> under <see cref="BenchRoot" />.</summary>
    public string ResolvedCorpusRoot => CorpusRoot ?? Path.Combine(BenchRoot, ".corpus");

    /// <summary>The resolved results directory under <see cref="BenchRoot" />.</summary>
    public string ResultsRoot => Path.Combine(BenchRoot, "results");
}

/// <summary>
///     A single evaluation suite. Suites are corpus- or fixture-bound; a suite that needs a resource
///     that is absent returns a <see cref="SuiteResult" /> whose notes explain the skip rather than
///     throwing, so the normal offline test run stays fast.
/// </summary>
public interface IEvalSuite
{
    /// <summary>The suite name, used on the command line and in result file names.</summary>
    string Name { get; }

    /// <summary>A one-line description of what the suite measures.</summary>
    string Description { get; }

    /// <summary>
    ///     Runs the suite.
    /// </summary>
    /// <param name="options">The run options.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The suite result.</returns>
    Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken);
}
