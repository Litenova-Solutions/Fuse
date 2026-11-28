using Microsoft.CodeAnalysis;

namespace Fuse.Workspace.Views;

/// <summary>An isolated transient HEAD solution whose backing workspace releases every compiled state on disposal.</summary>
internal sealed class TransientBaseline : IDisposable
{
    private readonly IDisposable _owner;

    public TransientBaseline(IDisposable owner, Solution solution)
    {
        _owner = owner;
        Solution = solution;
    }

    /// <summary>The transient HEAD solution snapshot.</summary>
    public Solution Solution { get; }

    public void Dispose() => _owner.Dispose();
}
