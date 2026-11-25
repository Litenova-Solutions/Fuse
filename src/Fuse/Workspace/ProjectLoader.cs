using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Workspace;

/// <summary>
///     Evaluates the repository's projects and opens the ones requests need into a lean
///     <see cref="BlueprintWorkspace"/> backed by <see cref="CompilationBlueprint"/> snapshots extracted from ephemeral
///     MSBuild runs. Unrestored projects without a matching zero-restore blueprint, or projects that MSBuild could not
///     evaluate, are refused rather than checked. Exotic projects that fail blueprint extraction fall back gracefully
///     to direct MSBuild loading.
/// </summary>
internal sealed class ProjectLoader : IDisposable
{
    private readonly RepoRoot _root;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<RepoPath, byte> _loaded = new();

    /// <summary>
    ///     Why each project that did not load failed, by project file. The loader's failure handler writes it while a load
    ///     holds the load lock, which may be a background load, and a request reads it without that lock, so it is
    ///     concurrent.
    /// </summary>
    private readonly ConcurrentDictionary<RepoPath, string> _loadFailures = new();

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly BlueprintWorkspace _blueprintWorkspace;
    private readonly Func<string?>? _headProvider;
    private readonly Func<IReadOnlySet<RepoPath>>? _dirtyProvider;
    private MSBuildWorkspace? _msbuildFallback;
    private CompilationBlueprint? _activeBlueprint;

    // Global properties for the fallback workspace, as in BlueprintEvaluator: the design-time build
    // properties keep the CoreCompile execution pass from compiling, and DisableMsCoverageReferencedPathMaps
    // keeps MsCoverageReferencedPathMaps from writing .msCoverageSourceRootsMapping_* into bin/.
    private static readonly Dictionary<string, string> DesignTimeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DesignTimeBuild"] = "true",
        ["BuildingProject"] = "false",
        ["SkipCompilerExecution"] = "true",
        ["ProvideCommandLineArgs"] = "true",
        ["DisableMsCoverageReferencedPathMaps"] = "true",
    };

    public ProjectLoader(
        RepoRoot root,
        Action<string> log,
        BlueprintWorkspace.SharedResources? sharedResources = null,
        Func<string?>? headProvider = null,
        Func<IReadOnlySet<RepoPath>>? dirtyProvider = null)
    {
        _root = root;
        _log = log;
        _blueprintWorkspace = new BlueprintWorkspace(sharedResources);
        _headProvider = headProvider;
        _dirtyProvider = dirtyProvider;
    }

    /// <summary>Shared immutable metadata, analyzer and HEAD text resources for this hypervisor.</summary>
    public BlueprintWorkspace.SharedResources SharedResources => _blueprintWorkspace.Shared;

    /// <summary>The active blueprint supplying loaded projects, or null when not loaded from a blueprint.</summary>
    public CompilationBlueprint? ActiveBlueprint => _activeBlueprint;

    /// <summary>True when the loader mapped projects onto another worktree's restored blueprint without local restore assets.</summary>
    public bool IsUsingZeroRestoreBlueprint { get; private set; }

    /// <summary>True when active projects come from a cached blueprint rather than the live MSBuild fallback workspace.</summary>
    public bool IsLoadedFromBlueprint => _activeBlueprint is not null && _msbuildFallback is null;

    /// <summary>Every C# project in the repository, as the last evaluation found it.</summary>
    public RepoGraph Graph { get; private set; } = null!;

    /// <summary>
    ///     Increments whenever every project is closed, because projects were evaluated again or reloaded, which
    ///     invalidates anything derived from project configuration.
    /// </summary>
    public int ConfigurationGeneration { get; private set; }

    /// <summary>
    ///     The loader's solution: every project opened since the last reset, each file as it was when its project
    ///     opened. Null until a load opens a project after a reset.
    /// </summary>
    public Solution? Solution =>
        _msbuildFallback?.CurrentSolution
        ?? (_blueprintWorkspace.LoadedPaths().Count > 0 ? _blueprintWorkspace.Solution : null);

    /// <summary>True when <paramref name="node"/> is open in the loader.</summary>
    public bool IsLoaded(ProjectNode node) => _loaded.ContainsKey(node.Path);

    /// <summary>The project files open in the loader, which a reload opens again.</summary>
    public List<RepoPath> LoadedPaths() => [.. _loaded.Keys];

    /// <summary>Why <paramref name="node"/> did not load, or null when no load of it has failed.</summary>
    public string? LoadFailureOf(ProjectNode node) =>
        _loadFailures.TryGetValue(node.Path, out var failure) ? failure : null;

    /// <summary>Returns the masked assets hash recorded in the active blueprint for zero-restore worktrees.</summary>
    public string? AssetsHashFallback(RepoPath assetsFile) =>
        _activeBlueprint is { } bp && bp.AssetsHashes.TryGetValue(assetsFile.Relative, out var hash) ? hash : null;

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
        // Wait out a background load, so the workspace is never disposed under it.
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _blueprintWorkspace.Reset();
            _msbuildFallback?.Dispose();
            _msbuildFallback = null;
            _activeBlueprint = null;
            IsUsingZeroRestoreBlueprint = false;
            _loaded.Clear();
            _loadFailures.Clear();
            ConfigurationGeneration++;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Unloads individual projects from the underlying <see cref="AdhocWorkspace"/> without resetting configuration.</summary>
    public async Task<IReadOnlyList<RepoPath>> UnloadAsync(IReadOnlyCollection<RepoPath> projectPaths, CancellationToken cancellationToken)
    {
        if (projectPaths.Count == 0 || _msbuildFallback is not null)
            return [];
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var unloaded = _blueprintWorkspace.UnloadProjects(projectPaths);
            foreach (var path in unloaded)
                _loaded.TryRemove(path, out _);
            return unloaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Drops transient HEAD text caches and unreferenced metadata references held by the loader.</summary>
    public void TrimCaches() => _blueprintWorkspace.TrimCaches();

    /// <summary>Opens <paramref name="projects"/> and everything they reference, unless they are open already.</summary>
    /// <remarks>
    ///     Each project that opens is in <see cref="Solution"/> at once, even when a later one fails or the request is
    ///     cancelled, so the caller compares the views with <see cref="Solution"/> rather than asking whether this call
    ///     opened anything.
    /// </remarks>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task LoadAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects.DistinctBy(p => p.Path).ToList();
        foreach (var project in requested)
        {
            if (_loadFailures.TryGetValue(project.Path, out var failure))
                throw LoadFailed(project, failure);
        }

        var missing = requested.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
        if (missing.Count > 0)
            await OpenAsync(missing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Opens <paramref name="project"/> outside a request, so it can run while requests are served. Neither view holds
    ///     it until the next request derives them again, which that request does because the views are no longer derived
    ///     from <see cref="Solution"/>.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public async Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken)
    {
        if (_loaded.ContainsKey(project.Path))
            return;
        await OpenAsync([project], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Opens <paramref name="projects"/> outside a request (see <see cref="PreloadAsync(ProjectNode, CancellationToken)"/>),
    ///     in one batch under one hold of the load lock.
    /// </summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task PreloadAsync(IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var missing = projects.DistinctBy(p => p.Path).Where(p => !_loaded.ContainsKey(p.Path)).ToList();
        if (missing.Count == 0)
            return;
        await OpenAsync(missing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Extracts the blueprints covering <paramref name="projects"/> and their closures into the shared cache, without
    ///     opening anything and without the load lock, so background preloading extracts while requests are served. A
    ///     later load opens from the cache without extracting again. Per-project extraction failures are recorded for
    ///     the loads to report, and a failure a later extraction healed is forgotten; only a missing restore is
    ///     thrown. A transient host failure is neither recorded nor thrown, so the later load extracts again.
    /// </summary>
    /// <exception cref="FuseException">A project has not been restored and no worktree blueprint covers it.</exception>
    public async Task EnsureExtractedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects.DistinctBy(p => p.Path).ToList();
        if (requested.Count == 0)
            return;
        var closure = requested.SelectMany(Graph.ClosureOf).DistinctBy(p => p.Path).ToList();
        var unrestored = closure.Where(p => !File.Exists(p.AssetsFile.Absolute)).Select(p => p.Path.Relative).Distinct(StringComparer.Ordinal).ToList();
        if (unrestored.Count > 0)
        {
            if (BlueprintEvaluator.TryFindReusableBlueprint(_root, Graph, requested) is null)
                throw RestoreNeeded(unrestored);
            return;
        }

        var missing = closure.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
        if (missing.Count == 0)
            return;
        var outcome = await BlueprintEvaluator.EnsureProjectsAsync(_root, Graph, missing, _log, cancellationToken).ConfigureAwait(false);
        RecordOutcome(outcome, requested);
    }

    /// <summary>Opens each of <paramref name="missing"/> that is not open yet, extracting first without the load lock.</summary>
    private async Task OpenAsync(IReadOnlyList<ProjectNode> missing, CancellationToken cancellationToken)
    {
        var closure = missing.SelectMany(Graph.ClosureOf).DistinctBy(p => p.Path).ToList();
        var unrestored = closure.Where(p => !File.Exists(p.AssetsFile.Absolute)).Select(p => p.Path.Relative).Distinct(StringComparer.Ordinal).ToList();
        CompilationBlueprint? reusableZeroRestore = null;
        if (unrestored.Count > 0)
        {
            reusableZeroRestore = BlueprintEvaluator.TryFindReusableBlueprint(_root, Graph, missing);
            if (reusableZeroRestore is null)
                throw RestoreNeeded(unrestored);
        }

        // Brings the shared cache up to date before taking the load lock: a request that arrives while the
        // background preload extracts joins that extraction instead of running its own under the lock, so the
        // preload's own loads are never blocked behind it, and the lock then only covers the short load.
        CompilationBlueprint? extracted = null;
        if (_msbuildFallback is null && reusableZeroRestore is null)
        {
            var unopened = missing.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
            if (unopened.Count > 0)
            {
                var preOutcome = await BlueprintEvaluator.EnsureProjectsAsync(_root, Graph, unopened, _log, cancellationToken).ConfigureAwait(false);
                RecordOutcome(preOutcome, unopened);
                ThrowOnRecordedFailures(unopened);
                ThrowOnTransient(preOutcome, unopened);
                extracted = preOutcome.Blueprint;
            }
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stillMissing = missing.Where(p => !_loaded.ContainsKey(p.Path)).ToList();
            if (stillMissing.Count == 0)
                return;

            var loadedViaBlueprint = false;
            if (_msbuildFallback is null)
            {
                try
                {
                    CompilationBlueprint? blueprint;
                    if (reusableZeroRestore is not null)
                    {
                        blueprint = reusableZeroRestore;
                        IsUsingZeroRestoreBlueprint = true;
                        _log($"zero-restore: mapped {stillMissing.Count} project(s) onto blueprint from {blueprint.SourceRoot}");
                    }
                    else
                    {
                        blueprint = extracted;
                        if (blueprint is null || !blueprint.Covers(_root, stillMissing.SelectMany(Graph.ClosureOf), allowZeroRestore: false))
                        {
                            // The cache lost coverage between the lock-free extraction and the lock (a reset
                            // unloaded projects, or a concurrent writer replaced the file): extract the remainder.
                            var outcome = await BlueprintEvaluator.EnsureProjectsAsync(_root, Graph, stillMissing, _log, cancellationToken).ConfigureAwait(false);
                            RecordOutcome(outcome, stillMissing);
                            ThrowOnRecordedFailures(stillMissing);
                            ThrowOnTransient(outcome, stillMissing);
                            blueprint = outcome.Blueprint;
                        }
                    }

                    if (blueprint is not null && blueprint.Covers(_root, stillMissing.SelectMany(Graph.ClosureOf), allowZeroRestore: reusableZeroRestore is not null))
                    {
                        // One project at a time, so a request that stops waiting in the middle leaves the rest
                        // unloaded for the next request instead of opening the whole batch.
                        foreach (var project in stillMissing)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (_loaded.ContainsKey(project.Path))
                                continue;
                            var projectStarted = Environment.TickCount64;
                            _blueprintWorkspace.LoadProjects(
                                _root,
                                Graph,
                                blueprint,
                                [project],
                                _headProvider?.Invoke(),
                                _dirtyProvider?.Invoke());
                            _activeBlueprint = blueprint;
                            PublishLoadedProjects(_blueprintWorkspace.Solution, project, projectStarted);
                        }

                        loadedViaBlueprint = true;
                    }
                }
                catch (FuseException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _log($"blueprint load fallback to MSBuild: {e.Message}");
                }
            }

            if (!loadedViaBlueprint)
                await OpenWithFallbackMsBuildAsync(stillMissing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void PublishLoadedProjects(Solution solution, ProjectNode requested, long startedTicks)
    {
        // Published after the workspace solution holds the project, so a request that finds it loaded also finds
        // the views behind that solution and derives them again. A load also opens the project's closure, so every
        // project the solution newly holds is published and logged, not just the requested one: nothing else reports
        // a closure member, and without its own line it would read as never loaded.
        foreach (var loadedProject in solution.Projects)
        {
            if (loadedProject.FilePath is null)
                continue;
            var loadedPath = _root.PathOf(loadedProject.FilePath);
            if (!_loaded.TryAdd(loadedPath, 0))
                continue;
            _loadFailures.TryRemove(loadedPath, out _);
            var name = requested.Path.Matches(loadedProject.FilePath)
                ? requested.Name
                : Graph.Find(_root.PathOf(loadedProject.FilePath))?.Name ?? Path.GetFileNameWithoutExtension(loadedProject.FilePath);
            _log($"loaded {name} in {Environment.TickCount64 - startedTicks} ms ({solution.ProjectIds.Count} projects open)");
        }
    }

    private void ThrowOnRecordedFailures(IReadOnlyList<ProjectNode> missing)
    {
        foreach (var node in missing.SelectMany(Graph.ClosureOf))
        {
            if (_loadFailures.TryGetValue(node.Path, out var failure))
                throw LoadFailed(node, failure);
        }
    }

    /// <summary>
    ///     Records <paramref name="outcome"/>'s deterministic failures and forgets the recorded failure of every
    ///     project in <paramref name="roots"/>' closures that the outcome extracted without failing, so a failure a
    ///     later extraction healed loads again. A transient host failure is never recorded.
    /// </summary>
    private void RecordOutcome(BlueprintEvaluator.ExtractionOutcome outcome, IEnumerable<ProjectNode> roots)
    {
        foreach (var (failedPath, reason) in outcome.Failures)
            _loadFailures[failedPath] = reason;
        if (outcome.Blueprint is not { } blueprint)
            return;
        var captured = blueprint.Projects.Select(p => p.ProjectPath).ToHashSet(StringComparer.Ordinal);
        foreach (var node in roots.SelectMany(Graph.ClosureOf).DistinctBy(p => p.Path))
        {
            if (!outcome.Failures.ContainsKey(node.Path) && captured.Contains(node.Path.Relative))
                _loadFailures.TryRemove(node.Path, out _);
        }
    }

    /// <summary>
    ///     Throws for a transient host failure that kept a project <paramref name="missing"/> needs out of
    ///     <paramref name="outcome"/>, without recording it, so the next request extracts again.
    /// </summary>
    /// <exception cref="FuseException">A project could not be extracted for a transient host failure.</exception>
    private void ThrowOnTransient(BlueprintEvaluator.ExtractionOutcome outcome, IReadOnlyList<ProjectNode> missing)
    {
        if (outcome.TransientFailure is not { } transient)
            return;
        var captured = outcome.Blueprint?.Projects.Select(p => p.ProjectPath).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var node in missing.SelectMany(Graph.ClosureOf))
        {
            if (!captured.Contains(node.Path.Relative))
                throw LoadFailed(node, transient);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task OpenWithFallbackMsBuildAsync(IReadOnlyList<ProjectNode> missing, CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();
        return OpenWithFallbackMsBuildCoreAsync(missing, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureMsBuildRegistered() => MsBuildSetup.EnsureRegistered();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task OpenWithFallbackMsBuildCoreAsync(IReadOnlyList<ProjectNode> missing, CancellationToken cancellationToken)
    {
        var loader = _msbuildFallback ??= CreateFallbackLoader();
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

            PublishLoadedProjects(loader.CurrentSolution, project, started);
        }

        ThrowOnRecordedFailures(missing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private MSBuildWorkspace CreateFallbackLoader()
    {
        var loader = MSBuildWorkspace.Create(DesignTimeProperties);
        loader.LoadMetadataForReferencedProjects = false;
        loader.SkipUnrecognizedProjects = true;
        loader.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;
            _log($"workspace: {e.Diagnostic.Message}");
            var path = Graph.Projects.FirstOrDefault(p => e.Diagnostic.Message.Contains(p.Path.Absolute, StringComparison.OrdinalIgnoreCase))?.Path;
            if (path is { } failed && !LoadFailure.IsWarning(e.Diagnostic.Message) && !BlueprintEvaluator.IsTransient(e.Diagnostic.Message))
                _loadFailures[failed] = e.Diagnostic.Message;
        });
        return loader;
    }

    private static FuseException RestoreNeeded(List<string> unrestored) =>
        new(
            ErrorCode.RestoreNeeded,
            $"restore needed: run `dotnet restore {unrestored[0]}`{(unrestored.Count > 1 ? " and the same for each project listed" : "")} ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

    private static FuseException LoadFailed(ProjectNode project, string reason) =>
        new(ErrorCode.LoadFailed, $"could not load {project.Path.Relative}: {reason}");

    public void Dispose()
    {
        _msbuildFallback?.Dispose();
        _blueprintWorkspace.Dispose();
    }
}
