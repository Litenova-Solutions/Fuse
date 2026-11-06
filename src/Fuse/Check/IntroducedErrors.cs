using System.Collections.Concurrent;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Check;

/// <summary>
///     Binds files or whole projects in the current solution and keeps the errors with no match at HEAD. A file is bound
///     in the baseline only when it has errors, and the baseline results are cached, because HEAD does not change between
///     edits. Each call reads the views of the generation it is given and counts in the context it is given, so calls
///     for different checks run at the same time without sharing either.
/// </summary>
internal sealed class IntroducedErrors
{
    private readonly RepoWorkspace _workspace;
    private readonly DiagnosticCollector _collector;
    private readonly Lock _baselineKeyLock = new();
    private int _baselineKeyGeneration = -1;
    private string? _baselineKeyHead;
    private string? _baselineKey;

    public IntroducedErrors(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _collector = new DiagnosticCollector(workspace.Root, () => workspace.ConfigurationGeneration, workspace.Log);
    }

    /// <summary>Time spent binding and running analyzers since the last call, summed over files, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings() => _collector.TakeTimings();

    /// <summary>Time spent updating the compilation, binding and running analyzers since the last call, and how many documents were bound, for the engine log.</summary>
    public (long UpdateMs, long CompilerMs, long AnalyzerMs, int Documents) TakeBindParts() => _collector.TakeBindParts();

    /// <inheritdoc cref="DiagnosticCollector.Begin"/>
    public void Begin(AnalyzerRun run, bool reuse) => _collector.Begin(run, reuse);

    /// <summary>Prepares the shared caches for <paramref name="context"/>.</summary>
    public void BeginContext(CheckContext context) => _collector.BeginContext(context);

    /// <inheritdoc cref="DiagnosticCollector.TakeReused"/>
    public int TakeReused() => _collector.TakeReused();

    /// <inheritdoc cref="DiagnosticCollector.TakeAnalyzerReport"/>
    public (int Unfinished, IReadOnlyList<string> LeftOut) TakeAnalyzerReport() => _collector.TakeAnalyzerReport();

    /// <inheritdoc cref="DiagnosticCollector.WarmBaselineProjectAsync(Project, int, CancellationToken)"/>
    public Task<bool> WarmBaselineAsync(Project project, int generation, CancellationToken cancellationToken) =>
        _collector.WarmBaselineProjectAsync(project, generation, cancellationToken);

    /// <summary>Warms one baseline project for <paramref name="context"/>.</summary>
    public Task<bool> WarmBaselineAsync(Project project, int generation, CheckContext context, CancellationToken cancellationToken) =>
        _collector.WarmBaselineProjectAsync(project, generation, context, cancellationToken);

    /// <summary>
    ///     Writes how many stored baseline results the check read and how many it computed, when it read the store.
    /// </summary>
    public void LogBaseline() => LogBaseline(_collector.TakeBaselineReport());

    /// <summary>Writes the baseline line for <paramref name="context"/>, when it read the store.</summary>
    public void LogBaseline(CheckContext context) => LogBaseline(context.TakeBaselineReport());

    private void LogBaseline((int Stored, int Computed) report)
    {
        var (stored, computed) = report;
        if (stored + computed == 0 || _workspace.Tracker.Head is not { } head)
            return;
        _workspace.Log($"baseline: {stored} stored result(s) read for HEAD {head[..Math.Min(12, head.Length)]}; {computed} computed");
    }

    /// <summary>The store key of <paramref name="generation"/>'s baseline, computed once per baseline generation.</summary>
    private string? BaselineKey(Generation generation)
    {
        lock (_baselineKeyLock)
        {
            if (_baselineKeyGeneration == generation.BaselineGeneration && _baselineKeyHead == generation.Head)
                return _baselineKey;
            var key = BaselineStore.KeyFor(generation.Head, generation.Baseline, generation.Graph, _workspace.Root);
            _baselineKeyGeneration = generation.BaselineGeneration;
            _baselineKeyHead = generation.Head;
            _baselineKey = key;
            return key;
        }
    }

    /// <summary>The introduced errors in <paramref name="paths"/>, bound in parallel; semantic models of different documents bind independently.</summary>
    /// <param name="paths">The files to bind. A file that no longer exists has no errors.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public Task<List<CompilerError>> InFilesAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken) =>
        InFilesAsync(paths, _workspace.PinGeneration(), new CheckContext(AnalyzerRun.Full, reuse: true), cancellationToken);

    /// <summary>The introduced errors in <paramref name="paths"/> of <paramref name="generation"/> for <paramref name="context"/>, bound in parallel.</summary>
    /// <param name="paths">The files to bind. A file that no longer exists has no errors.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public async Task<List<CompilerError>> InFilesAsync(IReadOnlyCollection<RepoPath> paths, Generation generation, CheckContext context, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<CompilerError>();
        var renames = await RenamesAsync(generation, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                foreach (var error in await InFileAsync(generation, path, renames, context, ct).ConfigureAwait(false))
                    results.Add(error);
            }).ConfigureAwait(false);
        return [.. results];
    }

    /// <summary>
    ///     The introduced errors in <paramref name="path"/> as <paramref name="view"/> has it, a solution derived from
    ///     <see cref="RepoWorkspace.Current"/> with some files at another content. A file the view does not hold has none.
    /// </summary>
    public async Task<List<CompilerError>> InFileOfAsync(Solution view, RepoPath path, CancellationToken cancellationToken)
    {
        var generation = _workspace.PinGeneration();
        var context = new CheckContext(AnalyzerRun.Full, reuse: true);
        var renames = await RenamesAsync(generation, cancellationToken).ConfigureAwait(false);
        return [.. await InFileAsync(view, generation, path, renames, context, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>The introduced errors in <paramref name="path"/> as <paramref name="view"/> has it, against <paramref name="generation"/>'s baseline for <paramref name="context"/>.</summary>
    public async Task<List<CompilerError>> InFileOfAsync(Solution view, RepoPath path, Generation generation, CheckContext context, CancellationToken cancellationToken)
    {
        var renames = await RenamesAsync(generation, cancellationToken).ConfigureAwait(false);
        return [.. await InFileAsync(view, generation, path, renames, context, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>The introduced errors in every file of <paramref name="node"/>, in each target framework, bound as whole compilations.</summary>
    public Task<List<CompilerError>> InProjectAsync(ProjectNode node, CancellationToken cancellationToken) =>
        InProjectAsync(node, _workspace.PinGeneration(), new CheckContext(AnalyzerRun.Full, reuse: true), cancellationToken);

    /// <summary>The introduced errors in every file of <paramref name="node"/> in <paramref name="generation"/> for <paramref name="context"/>.</summary>
    public async Task<List<CompilerError>> InProjectAsync(ProjectNode node, Generation generation, CheckContext context, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        var renames = await RenamesAsync(generation, cancellationToken).ConfigureAwait(false);
        var renamedFrom = renames.ToDictionary(r => r.Value.Relative, r => r.Key.Relative, StringComparer.Ordinal);
        foreach (var project in RepoWorkspace.ProjectsFor(generation.Current, node))
        {
            var current = await _collector.ForProjectAsync(project, context, cancellationToken).ConfigureAwait(false);
            if (current.Count == 0)
                continue;
            var baselineProject = generation.Baseline.GetProject(project.Id);
            var (baseline, finished) = baselineProject is null
                ? ((IReadOnlyList<CompilerError>)[], true)
                : await _collector.ForBaselineProjectAsync(baselineProject, generation.BaselineGeneration, context, cancellationToken, BaselineKey(generation)).ConfigureAwait(false);
            // An error HEAD had in a file renamed since then is matched under the file's new name.
            var (introduced, leftOut) = ErrorDelta.Introduced(current, baseline.Select(e => renamedFrom.TryGetValue(e.Path, out var renamed) ? e with { Path = renamed } : e), finished);
            if (leftOut)
                context.MarkUnfinished(project.Name);
            result.AddRange(introduced);
        }

        return result;
    }

    private Task<IEnumerable<CompilerError>> InFileAsync(Generation generation, RepoPath path, Dictionary<RepoPath, RepoPath> renames, CheckContext context, CancellationToken cancellationToken) =>
        File.Exists(path.Absolute) ? InFileAsync(generation.Current, generation, path, renames, context, cancellationToken) : Task.FromResult<IEnumerable<CompilerError>>([]);

    private async Task<IEnumerable<CompilerError>> InFileAsync(Solution solution, Generation generation, RepoPath path, Dictionary<RepoPath, RepoPath> renames, CheckContext context, CancellationToken cancellationToken)
    {
        var current = await _collector.ForFileAsync(solution, path, context, cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
            return [];
        // A file renamed since HEAD is compared with the errors HEAD had under its old name.
        var headPath = renames.TryGetValue(path, out var renamedFrom) ? renamedFrom : path;
        var (baseline, finished) = await _collector.ForBaselineFileAsync(generation.Baseline, generation.BaselineGeneration, headPath, context, cancellationToken, BaselineKey(generation)).ConfigureAwait(false);
        var (introduced, leftOut) = ErrorDelta.Introduced(current, headPath == path ? baseline : baseline.Select(e => e with { Path = path.Relative }), finished);
        if (leftOut)
            context.MarkUnfinished(path.Relative);
        return introduced;
    }

    /// <summary>
    ///     The files renamed since HEAD, from each one's working-tree path to its HEAD path. Git status runs without rename
    ///     detection, so a rename is a deleted file and a new one. A new file is taken as a rename of the deleted file whose
    ///     HEAD text shares at least half of the larger file's lines, the threshold git's rename detection uses, so the
    ///     errors HEAD had in it are not reported as introduced under its new name.
    /// </summary>
    private static async Task<Dictionary<RepoPath, RepoPath>> RenamesAsync(Generation generation, CancellationToken cancellationToken)
    {
        var renames = new Dictionary<RepoPath, RepoPath>();
        var baseline = generation.Baseline;
        var changed = generation.Changed.Where(p => PathRules.IsSource(p.Absolute)).ToList();
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
