using System.Collections.Concurrent;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Check;

/// <summary>
///     Binds files or whole projects in the current solution and keeps the errors with no match at HEAD. A file is bound
///     in the baseline only when it has errors, and the baseline results are cached, because HEAD does not change between
///     edits.
/// </summary>
internal sealed class IntroducedErrors
{
    private readonly RepoWorkspace _workspace;
    private readonly DiagnosticCollector _collector;

    public IntroducedErrors(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _collector = new DiagnosticCollector(workspace.Root, () => workspace.ConfigurationGeneration);
    }

    /// <summary>Time spent binding and running analyzers since the last call, summed over files, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings() => _collector.TakeTimings();

    /// <summary>The introduced errors in <paramref name="paths"/>, bound in parallel; semantic models of different documents bind independently.</summary>
    /// <param name="paths">The files to bind. A file that no longer exists has no errors.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public async Task<List<CompilerError>> InFilesAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<CompilerError>();
        var renames = await RenamesAsync(cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                foreach (var error in await InFileAsync(path, renames, ct).ConfigureAwait(false))
                    results.Add(error);
            }).ConfigureAwait(false);
        return [.. results];
    }

    /// <summary>The introduced errors in every file of <paramref name="node"/>, in each target framework, bound as whole compilations.</summary>
    public async Task<List<CompilerError>> InProjectAsync(ProjectNode node, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        var renames = await RenamesAsync(cancellationToken).ConfigureAwait(false);
        var renamedFrom = renames.ToDictionary(r => r.Value.Relative, r => r.Key.Relative, StringComparer.Ordinal);
        foreach (var project in RepoWorkspace.ProjectsFor(_workspace.Current, node))
        {
            var current = await _collector.ForProjectAsync(project, cancellationToken).ConfigureAwait(false);
            if (current.Count == 0)
                continue;
            var baselineProject = _workspace.Baseline.GetProject(project.Id);
            var baseline = baselineProject is null
                ? []
                : await _collector.ForBaselineProjectAsync(baselineProject, _workspace.BaselineGeneration, cancellationToken).ConfigureAwait(false);
            // An error HEAD had in a file renamed since then is matched under the file's new name.
            result.AddRange(ErrorDelta.Introduced(current, baseline.Select(e => renamedFrom.TryGetValue(e.Path, out var renamed) ? e with { Path = renamed } : e)));
        }

        return result;
    }

    private async Task<IEnumerable<CompilerError>> InFileAsync(RepoPath path, Dictionary<RepoPath, RepoPath> renames, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Absolute))
            return [];
        var current = await _collector.ForFileAsync(_workspace.Current, path, cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
            return [];
        // A file renamed since HEAD is compared with the errors HEAD had under its old name.
        var headPath = renames.TryGetValue(path, out var renamedFrom) ? renamedFrom : path;
        var baseline = await _collector.ForBaselineFileAsync(_workspace.Baseline, _workspace.BaselineGeneration, headPath, cancellationToken).ConfigureAwait(false);
        return ErrorDelta.Introduced(current, headPath == path ? baseline : baseline.Select(e => e with { Path = path.Relative })).ToList();
    }

    /// <summary>
    ///     The files renamed since HEAD, from each one's working-tree path to its HEAD path. Git status runs without rename
    ///     detection, so a rename is a deleted file and a new one. A new file is taken as a rename of the deleted file whose
    ///     HEAD text shares at least half of the larger file's lines, the threshold git's rename detection uses, so the
    ///     errors HEAD had in it are not reported as introduced under its new name.
    /// </summary>
    private async Task<Dictionary<RepoPath, RepoPath>> RenamesAsync(CancellationToken cancellationToken)
    {
        var renames = new Dictionary<RepoPath, RepoPath>();
        var baseline = _workspace.Baseline;
        var changed = _workspace.Tracker.Changed.Where(p => PathRules.IsSource(p.Absolute)).ToList();
        var added = changed.Where(p => File.Exists(p.Absolute) && baseline.GetDocumentIdsWithFilePath(p.Absolute).IsEmpty).ToList();
        var deleted = changed.Where(p => !File.Exists(p.Absolute) && !baseline.GetDocumentIdsWithFilePath(p.Absolute).IsEmpty).ToList();
        if (added.Count == 0 || deleted.Count == 0)
            return renames;

        var headLines = new Dictionary<RepoPath, Dictionary<string, int>>();
        foreach (var path in deleted)
        {
            if (baseline.GetDocument(baseline.GetDocumentIdsWithFilePath(path.Absolute)[0]) is { } document)
                headLines[path] = Lines((await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString());
        }

        foreach (var path in added)
        {
            var lines = Lines(await File.ReadAllTextAsync(path.Absolute, cancellationToken).ConfigureAwait(false));
            var best = headLines
                .Where(h => !renames.ContainsValue(h.Key))
                .Select(h => (Path: h.Key, Similarity: Similarity(lines, h.Value)))
                .Where(h => h.Similarity >= 0.5)
                .OrderByDescending(h => h.Similarity)
                .FirstOrDefault();
            if (best.Similarity > 0)
                renames[path] = best.Path;
        }

        return renames;
    }

    private static Dictionary<string, int> Lines(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                lines[trimmed] = lines.GetValueOrDefault(trimmed) + 1;
        }

        return lines;
    }

    /// <summary>The share of the larger file's lines the two files have in common, counting each line as often as it occurs.</summary>
    private static double Similarity(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var common = a.Sum(line => Math.Min(line.Value, b.GetValueOrDefault(line.Key)));
        var larger = Math.Max(a.Values.Sum(), b.Values.Sum());
        return larger == 0 ? 0 : (double)common / larger;
    }
}
