using System.Collections.Concurrent;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Workspace;

/// <summary>
///     Evaluates the repository's projects and opens the ones requests need in an MSBuildWorkspace. A project that is not
///     restored, or that MSBuild could not load, is refused rather than checked, because it would compile differently
///     from the real build.
/// </summary>
/// <remarks>
///     MSBuildWorkspace is used only as a loader. Its <c>TryApplyChanges</c> writes to disk, so it is never called;
///     <see cref="SolutionViews"/> derives both views from <see cref="Solution"/> instead.
/// </remarks>
internal sealed class ProjectLoader : IDisposable
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<RepoPath, byte> _loaded = new();
    private readonly Dictionary<RepoPath, string> _loadFailures = [];
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private MSBuildWorkspace? _loader;
    private volatile bool _preloaded;

    public ProjectLoader(RepoRoot root, Action<string> log)
    {
        _root = root;
        _log = log;
    }

    /// <summary>Every C# project in the repository, as the last evaluation found it.</summary>
    public RepoGraph Graph { get; private set; } = null!;

    /// <summary>
    ///     Increments whenever every project is closed, because projects were evaluated again or reloaded, which
    ///     invalidates anything derived from project configuration.
    /// </summary>
    public int ConfigurationGeneration { get; private set; }

    /// <summary>
    ///     The loader's solution: every project opened since the last reset, each file as it was on disk when its project
    ///     opened. Null until a load starts after a reset.
    /// </summary>
    public Solution? Solution => _loader?.CurrentSolution;

    /// <summary>True when <paramref name="node"/> is open in the loader.</summary>
    public bool IsLoaded(ProjectNode node) => _loaded.ContainsKey(node.Path);

    /// <summary>The project files open in the loader, which a reload opens again.</summary>
    public List<RepoPath> LoadedPaths() => [.. _loaded.Keys];

    /// <summary>Evaluates every project again and closes every project, so none stays open under the old evaluation. Compiles nothing.</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        Graph = await RepoGraph.EvaluateAsync(_root, cancellationToken).ConfigureAwait(false);
        _log($"evaluated {Graph.Projects.Count} projects in {Environment.TickCount64 - started} ms ({Graph.Failures.Count} failed)");
        foreach (var failure in Graph.Failures)
            _log($"evaluation failed: {failure}");
        _log("projects: " + string.Join(", ", Graph.Projects.Select(p => $"{p.Name} ({p.Sources.Count} sources{(p.IsTest ? ", tests" : "")}{(p.IsExecutable ? ", app" : "")})")));
        await ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes every project, forgets their load failures, and increments <see cref="ConfigurationGeneration"/>.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        // Wait out a background load, so the loader is never disposed under it.
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loader?.Dispose();
            _loader = null;
            _loaded.Clear();
            _loadFailures.Clear();
            _preloaded = false;
            ConfigurationGeneration++;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Opens <paramref name="projects"/> and everything they reference, unless they are open already.</summary>
    /// <returns>True when one of them was not open, so both views have to be derived from <see cref="Solution"/> again.</returns>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task<bool> LoadAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects.DistinctBy(p => p.Path).ToList();
        foreach (var project in requested)
        {
            if (_loadFailures.TryGetValue(project.Path, out var failure))
                throw LoadFailed(project, failure);
        }

        var missing = requested.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
        if (missing.Count == 0)
            return false;
        await OpenAsync(missing, background: false, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Opens <paramref name="project"/> outside a request, so it can run while requests are served. Neither view holds
    ///     it until the next request derives them again, which <see cref="TakePreloaded"/> tells that request to do.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public async Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken)
    {
        if (_loaded.ContainsKey(project.Path))
            return;
        await OpenAsync([project], background: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     True when <see cref="PreloadAsync"/> opened a project since the last call or reset. Neither view holds such a
    ///     project, so the caller derives both again. The answer is cleared, so each background load is folded in once.
    /// </summary>
    public bool TakePreloaded()
    {
        var preloaded = _preloaded;
        _preloaded = false;
        return preloaded;
    }

    /// <summary>Opens each of <paramref name="missing"/> that is not open yet, under the load lock.</summary>
    /// <param name="missing">The projects to open.</param>
    /// <param name="background">
    ///     True for a load outside a request. It marks each project it opens for <see cref="TakePreloaded"/> before the
    ///     project shows as loaded, because a request that finds a project loaded does not take the lock and folds it into
    ///     the views only when the mark is there.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the lock and loading.</param>
    private async Task OpenAsync(IReadOnlyList<ProjectNode> missing, bool background, CancellationToken cancellationToken)
    {
        var unrestored = missing.SelectMany(Graph.ClosureOf).Where(p => !File.Exists(p.AssetsFile.Absolute)).Select(p => p.Path.Relative).Distinct().ToList();
        // The command names a project, because a bare `dotnet restore` restores a solution, which may leave out the very
        // project that is missing its assets (a fuzzing or sample project outside the solution).
        if (unrestored.Count > 0)
            throw new FuseException(ErrorCode.RestoreNeeded, $"restore needed: run `dotnet restore {unrestored[0]}`{(unrestored.Count > 1 ? " and the same for each project listed" : "")} ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loader = _loader ??= CreateLoader();
            foreach (var project in missing)
            {
                if (_loaded.ContainsKey(project.Path))
                    continue;
                var started = Environment.TickCount64;
                try
                {
                    await loader.OpenProjectAsync(project.Path.Absolute, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
                {
                    throw LoadFailed(project, e.Message);
                }

                // Set before the project is published below: the loader's solution already holds it, so a request that
                // takes the mark now derives views that include it. The field is volatile and the dictionary write
                // follows it, so a request that sees the project loaded sees the mark too.
                if (background)
                    _preloaded = true;
                foreach (var loadedProject in loader.CurrentSolution.Projects)
                {
                    if (loadedProject.FilePath is not null)
                        _loaded[_root.PathOf(loadedProject.FilePath)] = 0;
                }

                _log($"loaded {project.Name} in {Environment.TickCount64 - started} ms ({loader.CurrentSolution.ProjectIds.Count} projects open)");
            }

            // A load failure (a missing project reference, an unresolvable SDK) leaves a project that compiles
            // differently from the real build, so it is reported rather than checked.
            foreach (var project in missing)
            {
                if (_loadFailures.TryGetValue(project.Path, out var failure))
                    throw LoadFailed(project, failure);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private MSBuildWorkspace CreateLoader()
    {
        var loader = MSBuildWorkspace.Create();
        loader.LoadMetadataForReferencedProjects = false;
        loader.SkipUnrecognizedProjects = true;
        loader.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;
            _log($"workspace: {e.Diagnostic.Message}");
            // The message names the project somewhere in its text, in whatever case MSBuild wrote it, so this is a search
            // of the text rather than a comparison of two paths.
            var path = Graph.Projects.FirstOrDefault(p => e.Diagnostic.Message.Contains(p.Path.Absolute, StringComparison.OrdinalIgnoreCase))?.Path;
            // MSBuildWorkspace reports every message MSBuild logged while loading, warnings included, as a failure of
            // the project. A warning leaves the project loadable, and the real build reports the same text as a warning
            // too, so recording it would make fuse decline to answer in a repository that builds. Only a project the
            // load could not evaluate is a failure; see LoadFailure.
            if (path is { } failed && !LoadFailure.IsWarning(e.Diagnostic.Message))
                _loadFailures[failed] = e.Diagnostic.Message;
        });
        return loader;
    }

    private static FuseException LoadFailed(ProjectNode project, string reason) =>
        new(ErrorCode.LoadFailed, $"could not load {project.Path.Relative}: {reason}");

    public void Dispose() => _loader?.Dispose();
}
