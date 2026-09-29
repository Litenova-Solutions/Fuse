using System.Collections.Concurrent;
using Fuse.Check.Model;
using Fuse.Graph;
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
        _collector = new DiagnosticCollector(workspace.Root, () => workspace.LoaderGeneration);
    }

    /// <summary>Time spent binding and running analyzers since the last call, summed over files, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings() => _collector.TakeTimings();

    /// <summary>The introduced errors in <paramref name="paths"/>, bound in parallel; semantic models of different documents bind independently.</summary>
    /// <param name="paths">Absolute paths. A file that no longer exists has no errors.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    public async Task<List<CompilerError>> InFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<CompilerError>();
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                foreach (var error in await InFileAsync(path, ct).ConfigureAwait(false))
                    results.Add(error);
            }).ConfigureAwait(false);
        return [.. results];
    }

    /// <summary>The introduced errors in every file of <paramref name="node"/>, in each target framework, bound as whole compilations.</summary>
    public async Task<List<CompilerError>> InProjectAsync(ProjectNode node, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        foreach (var project in RepoWorkspace.ProjectsFor(_workspace.Current, node))
        {
            var current = await _collector.ForProjectAsync(project, cancellationToken).ConfigureAwait(false);
            if (current.Count == 0)
                continue;
            var baselineProject = _workspace.Baseline.GetProject(project.Id);
            var baseline = baselineProject is null
                ? []
                : await _collector.ForBaselineProjectAsync(baselineProject, _workspace.BaselineGeneration, cancellationToken).ConfigureAwait(false);
            result.AddRange(DiagnosticDelta.Introduced(current, baseline));
        }

        return result;
    }

    private async Task<IEnumerable<CompilerError>> InFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        var current = await _collector.ForFileAsync(_workspace.Current, path, cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
            return [];
        var baseline = await _collector.ForBaselineFileAsync(_workspace.Baseline, _workspace.BaselineGeneration, path, cancellationToken).ConfigureAwait(false);
        return DiagnosticDelta.Introduced(current, baseline).ToList();
    }
}
