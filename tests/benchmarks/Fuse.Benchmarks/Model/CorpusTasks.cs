namespace Fuse.Benchmarks;

/// <summary>
///     One verified fail-to-pass oracle task, persisted so a model-driven suite (the loop referendum, B1) can
///     replay the exact task set without re-mining and re-verifying the corpus. Each record is a task
///     <see cref="CorpusTaskExtractor" /> proved: the changed tests fail at <see cref="BaseCommit" /> (with the
///     tests applied) and pass at <see cref="MergeCommit" />, selected by <see cref="TestFilter" />.
/// </summary>
/// <param name="Repo">The repository name (matches a corpus manifest entry).</param>
/// <param name="BaseCommit">The base commit the agent starts from (the change not yet applied).</param>
/// <param name="MergeCommit">The merge commit where the change and its tests are present and green.</param>
/// <param name="TestFilter">The <c>dotnet test --filter</c> expression selecting the changed tests.</param>
/// <param name="Title">The commit subject, used as the task prompt.</param>
/// <param name="TestFiles">
///     The changed test files (repo-relative) at the merge commit (D22a). The loop oracle post-check checks these
///     out onto the agent's edited worktree and runs <see cref="TestFilter" /> to compute true pass@1. A task set
///     written before D22a has none; the loop suite then skips the oracle for that task rather than guessing.
/// </param>
public sealed record CorpusTaskRecord(
    string Repo,
    string BaseCommit,
    string MergeCommit,
    string TestFilter,
    string Title,
    IReadOnlyList<string> TestFiles);

/// <summary>
///     The replayable set of verified oracle tasks (C4/B1), written to <c>results/corpus-tasks-v2.json</c> by the
///     corpus-health oracle-task pass and consumed by <see cref="LoopSuite" />. Persisting the set (rather than
///     re-mining each run) makes the loop referendum reproducible against a fixed task list and lets a long run
///     resume without repeating the multi-hour verification pass.
/// </summary>
/// <param name="Generated">The ISO-8601 UTC time the set was produced.</param>
/// <param name="Tasks">The verified tasks.</param>
public sealed record CorpusTaskSet(
    string Generated,
    IReadOnlyList<CorpusTaskRecord> Tasks)
{
    /// <summary>The file name the set is written to under the results directory.</summary>
    public const string FileName = "corpus-tasks-v2.json";
}
