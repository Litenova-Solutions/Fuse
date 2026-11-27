using Fuse.Graph;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Whether the repository projects have been evaluated. The loader starts without them and holds them
///     after the first evaluation, so code that needs them names the missing case instead of reading a null.
/// </summary>
internal abstract record LoadedWorkspaceState
{
    private LoadedWorkspaceState()
    {
    }

    /// <summary>No evaluation has finished yet, so there is no project graph to read.</summary>
    public sealed record NotLoaded : LoadedWorkspaceState;

    /// <summary>The evaluated projects, as the last evaluation found them.</summary>
    /// <param name="Graph">Every C# project in the repository, as the last evaluation found it.</param>
    public sealed record Loaded(RepoGraph Graph) : LoadedWorkspaceState;
}
