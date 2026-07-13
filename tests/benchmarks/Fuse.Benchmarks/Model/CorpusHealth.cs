namespace Fuse.Benchmarks;

/// <summary>
///     One repository's health in a corpus-health report (C4): the achieved index tier and its test-suite
///     discovery, so the harness can prove a repository is a usable arena (builds at tier-1, has runnable tests)
///     before a model-driven suite spends tokens on it.
/// </summary>
/// <param name="Name">The repository name.</param>
/// <param name="Tier">The achieved index mode: <c>semantic</c> (tier-1), <c>partial</c>, or <c>syntax</c>.</param>
/// <param name="Tier1">Whether the repository reached tier-1 (semantic) on the runner.</param>
/// <param name="TestProjects">The number of test projects discovered (csproj under a test path).</param>
/// <param name="TestFiles">The number of test source files discovered.</param>
/// <param name="Note">A short per-repo note (for example the restore or build outcome), or null.</param>
public sealed record CorpusRepoHealth(
    string Name,
    string Tier,
    bool Tier1,
    int TestProjects,
    int TestFiles,
    string? Note = null);

/// <summary>
///     The corpus-health report (C4): the machine-readable proof that the benchmark corpus is a usable arena for
///     the model-driven suites. It records, per repository, the achieved index tier and test discovery, plus the
///     verified-task count, and whether the corpus meets the minimums a model-driven suite requires. Written to
///     <c>results/corpus-health.json</c> by <c>fuse eval corpus-health</c>; a model-driven suite refuses to start
///     unless a report newer than the corpus manifest reports <see cref="MeetsMinimums" /> true.
/// </summary>
/// <param name="Generated">The ISO-8601 UTC time the report was produced.</param>
/// <param name="ReposTotal">The repositories in the corpus manifest.</param>
/// <param name="ReposTier1">The repositories that reached tier-1 (semantic) on the runner.</param>
/// <param name="TasksTotal">The candidate oracle tasks considered.</param>
/// <param name="TasksVerified">The oracle tasks verified mechanically (new or changed tests fail on base, pass on merge).</param>
/// <param name="MinReposTier1">The minimum tier-1 repositories the gate requires.</param>
/// <param name="MinTasksVerified">The minimum verified oracle tasks the gate requires.</param>
/// <param name="Repos">The per-repository health.</param>
/// <param name="Notes">Free-text notes (skips, environment facts, the reduced-scope decision).</param>
/// <param name="ManifestSha256">
///     SHA-256 of the corpus manifest used for the run. This survives checkout timestamp changes and is the
///     preferred freshness check for model-driven suites.
/// </param>
public sealed record CorpusHealthReport(
    string Generated,
    int ReposTotal,
    int ReposTier1,
    int TasksTotal,
    int TasksVerified,
    int MinReposTier1,
    int MinTasksVerified,
    IReadOnlyList<CorpusRepoHealth> Repos,
    IReadOnlyList<string> Notes,
    string? ManifestSha256 = null)
{
    /// <summary>The gate minimum for tier-1 repositories (C4).</summary>
    public const int GateMinReposTier1 = 20;

    /// <summary>The gate minimum for verified oracle tasks (C4).</summary>
    public const int GateMinTasksVerified = 60;

    /// <summary>
    ///     The pre-registered reduced-scope floor (C4/D20): with at least this many verified oracle tasks but below
    ///     the full minimums, a model-driven suite may still run as a no-headline pilot with confidence intervals.
    ///     Below this floor there is no usable arena and the suite refuses.
    /// </summary>
    public const int ReducedScopeTaskFloor = 40;

    /// <summary>The file name the report is written to under the results directory.</summary>
    public const string FileName = "corpus-health.json";

    /// <summary>
    ///     Whether the corpus meets the minimums a model-driven suite requires: at least
    ///     <see cref="MinReposTier1" /> repositories at tier-1 and at least <see cref="MinTasksVerified" />
    ///     verified oracle tasks.
    /// </summary>
    public bool MeetsMinimums => ReposTier1 >= MinReposTier1 && TasksVerified >= MinTasksVerified;
}
