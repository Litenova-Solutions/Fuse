using System.Text.Json.Serialization;

namespace Fuse.Benchmarks;

/// <summary>
///     A pinned benchmark corpus: the set of repositories Fuse is evaluated against, with the exact
///     commit each is checked out at so a run is reproducible.
/// </summary>
/// <param name="Tokenizer">The token encoding used for token counts (for example <c>o200k_base</c>).</param>
/// <param name="Generated">The date the manifest was generated (ISO date).</param>
/// <param name="Repos">The pinned repositories.</param>
public sealed record CorpusManifest(
    string Tokenizer,
    string Generated,
    IReadOnlyList<CorpusRepo> Repos);

/// <summary>
///     One pinned repository in the <see cref="CorpusManifest" />.
/// </summary>
/// <param name="Name">The repository name; also the directory name under the corpus root.</param>
/// <param name="Url">The clone URL, or null for an in-repo local fixture.</param>
/// <param name="Commit">The pinned commit hash to check out, or null for a local fixture.</param>
/// <param name="Local">A repo-relative path for an in-repo local fixture, or null for a cloned repo.</param>
/// <param name="CsFiles">The recorded count of C# files at the pinned commit (for sanity checks).</param>
public sealed record CorpusRepo(
    string Name,
    string? Url = null,
    string? Commit = null,
    string? Local = null,
    [property: JsonPropertyName("cs_files")] int CsFiles = 0);

/// <summary>
///     The raw shape of one entry in <c>prs.json</c>: a real merged pull request reconstructed from
///     a corpus repo's merge history. This is the on-disk ground-truth record before it is lifted into
///     a <see cref="PrTask" />.
/// </summary>
/// <param name="Repo">The corpus repository name.</param>
/// <param name="Pr">The pull request number.</param>
/// <param name="Merge">The merge commit hash.</param>
/// <param name="Base">The base commit (merge parent 1).</param>
/// <param name="Head">The head commit (merge parent 2).</param>
/// <param name="Title">The pull request title (head commit subject).</param>
/// <param name="ChangedCs">The C# files changed between base and head.</param>
public sealed record PrRecord(
    string Repo,
    int Pr,
    string Merge,
    string Base,
    string Head,
    string Title,
    [property: JsonPropertyName("changed_cs")] IReadOnlyList<string> ChangedCs);

/// <summary>
///     An evaluation dataset: a named set of repositories, each carrying the tasks evaluated against it.
/// </summary>
/// <param name="Name">The dataset name.</param>
/// <param name="Repos">The repositories and their tasks.</param>
public sealed record EvalDataset(
    string Name,
    IReadOnlyList<RepoTasks> Repos);

/// <summary>
///     One repository in an <see cref="EvalDataset" /> together with its resolved on-disk path and tasks.
/// </summary>
/// <param name="Id">A stable identifier for the repository within the dataset.</param>
/// <param name="Name">The repository name.</param>
/// <param name="Path">The absolute path to the checked-out repository, or null when the corpus is absent.</param>
/// <param name="Tasks">The tasks evaluated against this repository.</param>
public sealed record RepoTasks(
    string Id,
    string Name,
    string? Path,
    IReadOnlyList<PrTask> Tasks);

/// <summary>
///     A single evaluation task derived from a pull request: the change set plus its ground truth.
/// </summary>
/// <param name="Id">A stable task identifier (for example <c>MediatR#1171</c>).</param>
/// <param name="Kind">The task kind (currently always <c>pull_request</c>).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="Pr">The pull request number.</param>
/// <param name="BaseRef">The base commit ref.</param>
/// <param name="HeadRef">The head commit ref.</param>
/// <param name="MergeRef">The merge commit ref.</param>
/// <param name="Title">The pull request title.</param>
/// <param name="Body">The pull request body, when available.</param>
/// <param name="Category">The signal bucket the task falls into (see <see cref="SignalBucket" />).</param>
/// <param name="GroundTruth">The ground truth for the task.</param>
public sealed record PrTask(
    string Id,
    string Kind,
    string Repo,
    int Pr,
    string? BaseRef,
    string? HeadRef,
    string? MergeRef,
    string Title,
    string? Body,
    string Category,
    GroundTruth GroundTruth);

/// <summary>
///     The ground truth for a task: the files (with roles), symbols, routes, and services that define
///     a correct answer.
/// </summary>
/// <param name="Files">The relevant files with their roles.</param>
/// <param name="Symbols">Fully-qualified symbol names that are part of the answer.</param>
/// <param name="Routes">Route patterns that are part of the answer.</param>
/// <param name="Services">Service type names that are part of the answer.</param>
public sealed record GroundTruth(
    IReadOnlyList<GroundTruthFile> Files,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> Services);

/// <summary>
///     One file in a <see cref="GroundTruth" /> set, tagged with the role it plays in the change.
/// </summary>
/// <param name="Path">The repository-relative file path (forward slashes).</param>
/// <param name="Role">The role: <c>changed</c>, <c>test</c>, or a derived semantic role.</param>
public sealed record GroundTruthFile(
    string Path,
    string Role);

/// <summary>
///     The result of running one evaluation suite: a human-readable summary scorecard plus the
///     per-task records and free-text notes.
/// </summary>
/// <param name="Suite">The suite name (for example <c>review</c>).</param>
/// <param name="Description">A one-line description of what the suite measures.</param>
/// <param name="Generated">The date the run was produced (ISO date), or null when not stamped.</param>
/// <param name="Scorecard">The aggregate metrics.</param>
/// <param name="Tasks">The per-task results.</param>
/// <param name="Notes">Free-text notes (skips, warnings, environment facts).</param>
public sealed record SuiteResult(
    string Suite,
    string Description,
    string? Generated,
    Scorecard Scorecard,
    IReadOnlyList<TaskResult> Tasks,
    IReadOnlyList<string> Notes);

/// <summary>
///     The aggregate metrics for a suite run. Fields not relevant to a given suite are left at their
///     default of zero.
/// </summary>
/// <param name="TaskCount">The number of tasks scored.</param>
/// <param name="Recall">Mean file-level recall over the scored tasks.</param>
/// <param name="RecallCiLow">The low bound of the bootstrap confidence interval for recall.</param>
/// <param name="RecallCiHigh">The high bound of the bootstrap confidence interval for recall.</param>
/// <param name="Precision">Mean file-level precision over the scored tasks.</param>
/// <param name="F1">Mean F1 over the scored tasks.</param>
/// <param name="MedianTokens">The median returned-token count over the scored tasks.</param>
/// <param name="MeanTokens">The mean returned-token count over the scored tasks.</param>
/// <param name="LowSignalF1">The F1 of low-signal detection, when the suite measures it.</param>
public sealed record Scorecard(
    int TaskCount,
    double Recall,
    double RecallCiLow,
    double RecallCiHigh,
    double Precision,
    double F1,
    double MedianTokens,
    double MeanTokens,
    double LowSignalF1 = 0.0);

/// <summary>
///     The result of one task within a suite.
/// </summary>
/// <param name="Id">The task identifier.</param>
/// <param name="Repo">The repository name.</param>
/// <param name="Category">The signal bucket.</param>
/// <param name="Recall">File-level recall for this task.</param>
/// <param name="Precision">File-level precision for this task.</param>
/// <param name="Tokens">Returned tokens for this task.</param>
/// <param name="LatencyMs">Wall-clock latency for this task in milliseconds.</param>
/// <param name="Hit">Hit (relevant files returned), missed (ground-truth not returned), and extra (irrelevant) paths.</param>
public sealed record TaskResult(
    string Id,
    string Repo,
    string Category,
    double Recall,
    double Precision,
    int Tokens,
    long LatencyMs,
    TaskFiles Hit);

/// <summary>
///     The file-level breakdown for a task result.
/// </summary>
/// <param name="Hits">Ground-truth files that were returned.</param>
/// <param name="Misses">Ground-truth files that were not returned.</param>
/// <param name="Extras">Returned files that are not in the ground truth.</param>
public sealed record TaskFiles(
    IReadOnlyList<string> Hits,
    IReadOnlyList<string> Misses,
    IReadOnlyList<string> Extras);
