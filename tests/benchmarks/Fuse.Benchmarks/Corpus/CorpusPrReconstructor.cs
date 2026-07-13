using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fuse.Benchmarks;

/// <summary>
///     Reconstructs a review/localize/ranking PR ground-truth set from a corpus repository's merge history (D22c),
///     the same merge-commit method that produced the original <c>prs.json</c>: a first-parent commit whose diff
///     over its parent changes a bounded number of C# files and whose title is a real change (not a maintenance
///     commit) becomes a PR record whose changed C# files are the changed-file ground truth. This is the
///     changed-file half of <see cref="CorpusTaskExtractor" /> (which additionally verifies a fail-to-pass test
///     oracle); the review/localize/ranking suites score changed-file recall and precision, so they need only the
///     changed set, not a runnable test.
/// </summary>
/// <remarks>
///     The pure selection rules live here so they are unit-tested without a repository: the changed-C#-file count
///     must fall in [<see cref="MinChangedCsFiles" />, <see cref="MaxChangedCsFiles" />] (a one-file change is too
///     thin to score localization, a hundred-file change is a bulk move that swamps precision), and the title must
///     not be a maintenance commit (a merge bubble, a dependency or version bump, a revert, or a pure formatting
///     or changelog commit) that carries no localizable behavior. The git walk that feeds these rules is wired in
///     the suite/runner, which supplies each commit's title and changed C# files.
/// </remarks>
public static class CorpusPrReconstructor
{
    /// <summary>The minimum changed C# files for a commit to be a usable localization/review task.</summary>
    public const int MinChangedCsFiles = 2;

    /// <summary>The maximum changed C# files (a larger change is a bulk edit that swamps precision).</summary>
    public const int MaxChangedCsFiles = 25;

    // Titles that signal a maintenance commit with no localizable behavior change: merge bubbles, dependency and
    // version bumps, reverts, and pure formatting/changelog/whitespace commits. Matched case-insensitively against
    // the trimmed title. Kept deliberately conservative so a real change is never dropped as maintenance.
    private static readonly Regex MaintenanceTitle = new(
        @"^(merge\s+(pull\s+request|branch|remote|commit)|"
        + @"bump\b|update\s+dependenc|dependabot|"
        + @"revert\b|"
        + @"(prepare\s+)?release\b|"
        + @"(bump|update|set|prepare)\s+version|version\s+bump|"
        + @"^v?\d+\.\d+\.\d+|"
        + @"update\s+changelog|changelog\b|"
        + @"(fix\s+)?(formatting|whitespace|typo|typos)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    ///     Whether a commit title is a maintenance commit that carries no localizable behavior change and is
    ///     therefore dropped from the reconstructed PR set.
    /// </summary>
    /// <param name="title">The commit subject.</param>
    /// <returns>True to drop the commit; false to keep it as a candidate task.</returns>
    public static bool IsMaintenanceTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true; // A commit with no subject carries no task prompt.
        return MaintenanceTitle.IsMatch(title.Trim());
    }

    /// <summary>
    ///     Whether a commit's changed C# file count qualifies it as a review/localize/ranking task.
    /// </summary>
    /// <param name="changedCsFileCount">The number of C# files changed between the commit's base and head.</param>
    /// <returns>True when the count is in the usable band.</returns>
    public static bool QualifiesByFileCount(int changedCsFileCount) =>
        changedCsFileCount is >= MinChangedCsFiles and <= MaxChangedCsFiles;

    /// <summary>
    ///     Whether a candidate commit qualifies as a reconstructed PR task: a non-maintenance title and a changed
    ///     C# file count in the usable band.
    /// </summary>
    /// <param name="title">The commit subject.</param>
    /// <param name="changedCsFiles">The C# files changed between the commit's base and head.</param>
    /// <returns>True to keep the commit as a task.</returns>
    public static bool Qualifies(string title, IReadOnlyCollection<string> changedCsFiles) =>
        !IsMaintenanceTitle(title) && QualifiesByFileCount(changedCsFiles.Count);

    /// <summary>
    ///     Builds a <see cref="PrRecord" /> from a qualifying commit. The pull-request number is a synthetic,
    ///     stable ordinal (the corpus commits are not numbered PRs); the changed C# files are the ground truth.
    /// </summary>
    /// <param name="repo">The repository name.</param>
    /// <param name="ordinal">A stable ordinal used as the synthetic PR number.</param>
    /// <param name="mergeCommit">The commit (the head/merge state where the change is present).</param>
    /// <param name="baseCommit">The parent commit (the state before the change).</param>
    /// <param name="title">The commit subject, used as the task title.</param>
    /// <param name="changedCsFiles">The changed C# files (repo-relative, forward slashes).</param>
    /// <returns>The reconstructed record.</returns>
    public static PrRecord ToRecord(
        string repo, int ordinal, string mergeCommit, string baseCommit, string title, IReadOnlyList<string> changedCsFiles) =>
        new(repo, ordinal, mergeCommit, baseCommit, mergeCommit, title, changedCsFiles);

    /// <summary>
    ///     Reconstructs the PR records for one corpus repository by walking its first-parent history (D22c). For
    ///     each of the most recent <paramref name="scanCommits" /> first-parent commits, the commit's changed C#
    ///     files are its diff over its parent; a commit is kept as a task when it passes <see cref="Qualifies" />
    ///     (a real title and a banded changed-file count). Records are numbered by a stable descending ordinal so
    ///     the set is deterministic. The git walk mirrors <see cref="CorpusTaskExtractor" /> but records only the
    ///     changed-file ground truth the review/localize/ranking suites score, with no test-oracle verification.
    /// </summary>
    /// <param name="repoPath">The repository path (checked out at its pinned commit).</param>
    /// <param name="repoName">The repository name (stamped on each record).</param>
    /// <param name="scanCommits">How many first-parent commits to scan back from head.</param>
    /// <param name="maxTasks">The maximum records to return for this repository.</param>
    /// <param name="cancellationToken">A token to cancel the walk.</param>
    /// <returns>The reconstructed records, most recent first (empty when the log cannot be read).</returns>
    public static async Task<IReadOnlyList<PrRecord>> ReconstructAsync(
        string repoPath, string repoName, int scanCommits, int maxTasks, CancellationToken cancellationToken)
    {
        var log = await GitCli.RunAsync(repoPath, cancellationToken,
            "log", "--first-parent", $"-n{scanCommits}", "--format=%H %s");
        if (!log.Ok)
            return [];

        var records = new List<PrRecord>();
        var ordinal = scanCommits; // Descending, so the newest commit gets the highest synthetic PR number.
        foreach (var line in log.Lines)
        {
            ordinal--;
            if (records.Count >= maxTasks)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            // "<full-sha> <subject>"; a commit hash never contains a space, so split on the first.
            var sep = line.IndexOf(' ');
            var commit = (sep < 0 ? line : line[..sep]).Trim();
            var title = sep < 0 ? string.Empty : line[(sep + 1)..].Trim();
            if (commit.Length == 0 || IsMaintenanceTitle(title))
                continue; // Cheap title filter before the diff, so a maintenance commit costs no git diff.

            var names = await GitCli.RunAsync(repoPath, cancellationToken, "diff", "--name-only", $"{commit}^", commit);
            if (!names.Ok)
                continue;
            var changedCs = names.Lines
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Replace('\\', '/'))
                .ToList();
            if (!QualifiesByFileCount(changedCs.Count))
                continue;

            var parent = await GitCli.RunAsync(repoPath, cancellationToken, "rev-parse", $"{commit}^");
            if (!parent.Ok)
                continue;

            records.Add(ToRecord(repoName, Math.Max(1, ordinal), commit, parent.StdOut.Trim(), title, changedCs));
        }

        return records;
    }

    /// <summary>
    ///     Serializes a reconstructed PR set to the <c>prs.json</c> on-disk shape (a flat array of records), so a
    ///     review/localize/ranking run can consume it exactly as it consumes the retired dataset (D22c).
    /// </summary>
    /// <param name="path">The output file path.</param>
    /// <param name="records">The reconstructed records.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async Task WriteDatasetAsync(
        string path, IReadOnlyList<PrRecord> records, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            records.ToArray(), BenchmarkJsonContext.Default.PrRecordArray);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}
