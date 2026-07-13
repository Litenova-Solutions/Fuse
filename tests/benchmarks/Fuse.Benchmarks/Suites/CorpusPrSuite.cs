using System.Text.Json;

namespace Fuse.Benchmarks;

/// <summary>
///     Reconstructs a review/localize/ranking PR ground-truth set from the corpus-v2 repositories via the
///     merge-commit method (D22c) and writes it as a <c>prs.json</c>-shaped dataset the review, localize, and
///     ranking suites consume. Each present repository's first-parent history is walked by
///     <see cref="CorpusPrReconstructor" />: a non-maintenance commit changing a bounded number of C# files becomes
///     a task whose changed C# files are the changed-file ground truth. The output file (default
///     <c>prs-v2.json</c>) is then passed to those suites via <c>--dataset-file</c> together with the corpus-v2
///     manifest, so they score the buildable corpus rather than the retired dataset.
/// </summary>
public sealed class CorpusPrSuite : IEvalSuite
{
    private const int DefaultPerRepoCap = 10;

    /// <inheritdoc />
    public string Name => "corpus-prs";

    /// <inheritdoc />
    public string Description => "Reconstruct a review/localize/ranking PR set from the corpus-v2 merge history (D22c).";

    /// <inheritdoc />
    public async Task<SuiteResult> RunAsync(EvalOptions options, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var manager = new CorpusManager(options.BenchRoot, options.ResolvedCorpusRoot, options.Log);
        var manifest = manager.LoadManifest(options.ManifestPath);
        var perRepoCap = options.Limit > 0 ? options.Limit : DefaultPerRepoCap;

        var records = new List<PrRecord>();
        var perRepoCounts = new List<string>();
        foreach (var repo in manifest.Repos
                     .Where(r => options.RepoFilter is null || r.Name.Equals(options.RepoFilter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manager.IsPresent(repo))
            {
                notes.Add($"{repo.Name}: absent on disk; skipped.");
                continue;
            }

            var repoPath = manager.ResolveRepoPath(repo);
            var mined = await CorpusPrReconstructor.ReconstructAsync(
                repoPath, repo.Name, options.ScanCommits, perRepoCap, cancellationToken);
            records.AddRange(mined);
            perRepoCounts.Add($"{repo.Name} {mined.Count}");
            options.Report($"corpus-prs: {repo.Name} reconstructed {mined.Count} PR task(s)");
        }

        var outFile = options.DatasetFile ?? "prs-v2.json";
        var outPath = Path.Combine(options.BenchRoot, outFile);
        await CorpusPrReconstructor.WriteDatasetAsync(outPath, records, cancellationToken);

        notes.Add($"wrote {records.Count} PR record(s) across {perRepoCounts.Count} repo(s) to {outPath}");
        notes.Add($"per-repo: {string.Join(", ", perRepoCounts)}");
        notes.Add($"scan commits {options.ScanCommits}, per-repo cap {perRepoCap}, file-count band {CorpusPrReconstructor.MinChangedCsFiles}-{CorpusPrReconstructor.MaxChangedCsFiles}");

        // The scorecard carries the record count in the task-count slot; this suite writes a dataset, it does not
        // score recall, so the recall/precision fields stay zero.
        var scorecard = new Scorecard(records.Count, 0, 0, 0, 0, 0, 0, 0);
        return new SuiteResult(Name, Description, null, scorecard, [], notes);
    }
}
