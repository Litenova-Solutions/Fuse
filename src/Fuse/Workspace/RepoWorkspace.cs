using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

/// <summary>
///     The repository as the features see it: the evaluated projects, the files that differ from HEAD, and two views of
///     the loaded projects, <see cref="Current"/> (the working tree) and <see cref="Baseline"/> (the same projects with
///     every changed source restored to its HEAD content). Projects load when a check or test plan needs them, or in the
///     background ahead of time.
/// </summary>
/// <remarks>
///     It composes the parts and adds no rule of its own: <see cref="ChangeTracker"/> tracks which files differ from
///     HEAD, <see cref="ProjectLoader"/> evaluates the projects and opens them, <see cref="SolutionViews"/> holds both
///     views, and <see cref="WorkspaceSync"/> keeps them current. One writer lock serializes every sync and every
///     load that folds a project into the views, and each round that changes a view, the graph or the changed set
///     publishes a <see cref="Generation"/>. A reader pins a generation and reads only it, so it never sees a
///     half-applied sync and never waits for the writer after its pin.
/// </remarks>
internal sealed class RepoWorkspace : IDisposable
{
    private readonly Action<string> _log;
    private readonly ProjectLoader _projects;
    private readonly SolutionViews _views;
    private readonly WorkspaceSync _sync;

    /// <summary>Serializes sync and load, the writer rounds. A round holds it until it publishes its generation.</summary>
    private readonly SemaphoreSlim _writer = new(1, 1);

    private Generation? _generation;
    private long _generationNumber;

    public RepoWorkspace(RepoRoot root, Action<string> log)
    {
        Root = root;
        _log = log;
        Tracker = new ChangeTracker(root);
        _projects = new ProjectLoader(root, log);
        _views = new SolutionViews(root, Tracker, _projects, log);
        _sync = new WorkspaceSync(Tracker, _projects, _views, log);
    }

    public ChangeTracker Tracker { get; }

    public RepoGraph Graph => _projects.Graph;

    /// <summary>The loaded projects as they are on disk.</summary>
    public Solution Current => _views.Current;

    /// <summary>The loaded projects with every changed file at its HEAD content.</summary>
    public Solution Baseline => _views.Baseline;

    /// <inheritdoc cref="SolutionViews.BaselineGeneration"/>
    public int BaselineGeneration => _views.BaselineGeneration;

    /// <inheritdoc cref="ProjectLoader.ConfigurationGeneration"/>
    public int ConfigurationGeneration => _projects.ConfigurationGeneration;

    /// <summary>The summaries the views supply references to projects outside the change cone from.</summary>
    public SummaryCache Summaries => _views.Summaries;

    /// <inheritdoc cref="SolutionViews.UseSummaries"/>
    public bool UseSummaries
    {
        get => _views.UseSummaries;
        set => _views.UseSummaries = value;
    }

    public RepoRoot Root { get; }

    public void Log(string message) => _log(message);

    /// <summary>
    ///     The newest published generation. A check pins it when it starts work and holds it to the end, so the
    ///     views, graph and changed set it reads belong at one instant.
    /// </summary>
    public Generation PinGeneration() => Volatile.Read(ref _generation) ?? PublishInitial();

    /// <summary>Evaluates the project graph and starts tracking changes. Compiles nothing.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Tracker.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _sync.EvaluateAsync(cancellationToken).ConfigureAwait(false);
            PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <inheritdoc cref="WorkspaceSync.SyncAsync"/>
    public async Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken)
    {
        await SyncAndPinAsync(knownPaths, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Syncs the workspace under the writer lock and pins the generation that sync published.</summary>
    /// <param name="knownPaths">Files just written by the agent, checked even before their watcher event arrives.</param>
    /// <param name="cancellationToken">Cancels re-evaluation.</param>
    /// <param name="confirmWithDisk">True to confirm the changed set against disk when the watcher reported nothing, for a check of every change.</param>
    public async Task<Generation> SyncAndPinAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken, bool confirmWithDisk = false)
    {
        var known = knownPaths as IReadOnlyCollection<RepoPath> ?? [.. knownPaths];
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sync.SyncAsync(known, cancellationToken, confirmWithDisk).ConfigureAwait(false);
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        await EnsureLoadedAndPinAsync(projects, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads <paramref name="projects"/> under the writer lock and pins the generation that load published.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task<Generation> EnsureLoadedAndPinAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects as IReadOnlyCollection<ProjectNode> ?? [.. projects];
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sync.EnsureLoadedAsync(requested, cancellationToken).ConfigureAwait(false);
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Applies each of <paramref name="paths"/> to both views again: its disk content to <see cref="Current"/> and its HEAD content to <see cref="Baseline"/>.</summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task PatchViewsAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        await PatchAndPinAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies each of <paramref name="paths"/> under the writer lock and pins the generation it published.</summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task<Generation> PatchAndPinAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sync.PatchViewsAsync(paths, cancellationToken).ConfigureAwait(false);
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task RebuildViewsAsync(CancellationToken cancellationToken)
    {
        await RebuildAndPinAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Derives both views again under the writer lock and pins the generation it published, for a reader whose pinned generation lacks a reached project.</summary>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task<Generation> RebuildAndPinAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sync.RebuildAsync(cancellationToken).ConfigureAwait(false);
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    ///     Loads <paramref name="project"/> into the loader without touching <see cref="Current"/> or
    ///     <see cref="Baseline"/>, so it can run while requests are served; the next writer round folds it into both
    ///     views. The loader's own lock serializes the load itself, because MSBuild evaluation is not thread-safe.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken) =>
        _projects.PreloadAsync(project, cancellationToken);

    public bool IsLoaded(ProjectNode node) => _projects.IsLoaded(node);

    /// <summary>Why <paramref name="node"/> did not load, or null when no load of it has failed.</summary>
    public string? LoadFailureOf(ProjectNode node) => _projects.LoadFailureOf(node);

    /// <summary>The loaded Roslyn projects (one per target framework) built from <paramref name="node"/>.</summary>
    /// <remarks>
    ///     A Roslyn project keeps the path MSBuild evaluated, which is the node's own spelling, so the paths are compared
    ///     as spelled; this runs once per project of the solution for each reached project, on every check.
    /// </remarks>
    public static IEnumerable<Project> ProjectsFor(Solution solution, ProjectNode node) =>
        solution.Projects.Where(p => node.Path.Matches(p.FilePath));

    /// <summary>Sheds the session views under the writer lock, as the memory budget's shed order calls for after kept answers and caches.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearSessionViewsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _views.ClearSessionViews();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Sheds the HEAD compilations under the writer lock and publishes the generation it leaves, as the memory budget's shed order calls for after session views.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearHeadCompilationsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _views.ClearHeadCompilations();
            PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Sheds the idle cone projects under the writer lock and publishes the generation it leaves, as the memory budget's shed order calls for last.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearIdleConeProjectsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _views.ClearIdleConeProjects();
            PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <inheritdoc cref="SolutionViews.HeadText"/>
    public SourceText? HeadText(RepoPath path) => _views.HeadText(path);

    /// <inheritdoc cref="SolutionViews.CurrentWithHeadContentAsync(IEnumerable{RepoPath}, CancellationToken)"/>
    public Task<Solution> CurrentWithHeadContentAsync(IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        _views.CurrentWithHeadContentAsync(paths, cancellationToken);

    /// <summary>
    ///     <paramref name="generation"/>'s current view with each of <paramref name="paths"/> at its HEAD content, for a
    ///     reader that pinned that generation. Neither published view changes.
    /// </summary>
    public Task<Solution> CurrentWithHeadContentAsync(Generation generation, IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        _views.CurrentWithHeadContentAsync(generation.Current, paths, cancellationToken);

    /// <summary>
    ///     Publishes the state the writer just left as the newest generation, or keeps the current one when nothing
    ///     it holds changed. The caller holds the writer lock.
    /// </summary>
    private Generation PublishLocked()
    {
        var current = _views.Current;
        var baseline = _views.Baseline;
        var graph = _projects.Graph;
        var changed = Tracker.Changed;
        var nonSources = Tracker.NonSources;
        var head = Tracker.Head;
        var configuration = _projects.ConfigurationGeneration;
        var baselineGeneration = _views.BaselineGeneration;
        if (_generation is { } published
            && ReferenceEquals(published.Current, current)
            && ReferenceEquals(published.Baseline, baseline)
            && ReferenceEquals(published.Graph, graph)
            && published.Head == head
            && published.ConfigurationGeneration == configuration
            && published.BaselineGeneration == baselineGeneration
            && published.Changed.SequenceEqual(changed)
            && published.NonSources.SequenceEqual(nonSources))
            return published;

        var next = new Generation(
            _generationNumber++,
            head,
            current,
            baseline,
            graph,
            [.. changed],
            [.. nonSources],
            Tracker.NonSourceChanged,
            [.. Tracker.NonSourceSample],
            configuration,
            baselineGeneration);
        Volatile.Write(ref _generation, next);
        return next;
    }

    private Generation PublishInitial()
    {
        var empty = new AdhocWorkspace().CurrentSolution;
        var next = new Generation(
            _generationNumber++,
            Tracker.Head,
            (Solution?)_views.Current ?? empty,
            (Solution?)_views.Baseline ?? empty,
            _projects.Graph!,
            [.. Tracker.Changed],
            [.. Tracker.NonSources],
            Tracker.NonSourceChanged,
            [.. Tracker.NonSourceSample],
            _projects.ConfigurationGeneration,
            _views.BaselineGeneration);
        return Interlocked.CompareExchange(ref _generation, next, null) ?? next;
    }

    public void Dispose()
    {
        _projects.Dispose();
        Tracker.Dispose();
        _writer.Dispose();
    }
}
