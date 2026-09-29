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
///     views, and <see cref="WorkspaceSync"/> keeps them current.
/// </remarks>
internal sealed class RepoWorkspace : IDisposable
{
    private readonly Action<string> _log;
    private readonly ProjectLoader _projects;
    private readonly SolutionViews _views;
    private readonly WorkspaceSync _sync;

    public RepoWorkspace(RepoRoot root, Action<string> log)
    {
        Root = root;
        _log = log;
        Tracker = new ChangeTracker(root);
        _projects = new ProjectLoader(root, log);
        _views = new SolutionViews(root, Tracker, _projects);
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

    public RepoRoot Root { get; }

    public void Log(string message) => _log(message);

    /// <summary>Evaluates the project graph and starts tracking changes. Compiles nothing.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Tracker.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _sync.EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="WorkspaceSync.SyncAsync"/>
    public Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken) =>
        _sync.SyncAsync(knownPaths, cancellationToken);

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken) =>
        _sync.EnsureLoadedAsync(projects, cancellationToken);

    /// <summary>
    ///     Loads <paramref name="project"/> into the loader without touching <see cref="Current"/> or
    ///     <see cref="Baseline"/>, so it can run while requests are served; the next request folds it into both views.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken) =>
        _projects.PreloadAsync(project, cancellationToken);

    public bool IsLoaded(ProjectNode node) => _projects.IsLoaded(node);

    /// <summary>The loaded Roslyn projects (one per target framework) built from <paramref name="node"/>.</summary>
    /// <remarks>
    ///     A Roslyn project keeps the path MSBuild evaluated, which is the node's own spelling, so the paths are compared
    ///     as spelled; this runs once per project of the solution for each reached project, on every check.
    /// </remarks>
    public static IEnumerable<Project> ProjectsFor(Solution solution, ProjectNode node) =>
        solution.Projects.Where(p => node.Path.Matches(p.FilePath));

    /// <inheritdoc cref="SolutionViews.HeadText"/>
    public SourceText? HeadText(RepoPath path) => _views.HeadText(path);

    /// <inheritdoc cref="SolutionViews.Decode"/>
    internal static SourceText Decode(byte[] bytes) => SolutionViews.Decode(bytes);

    public void Dispose()
    {
        _projects.Dispose();
        Tracker.Dispose();
    }
}
