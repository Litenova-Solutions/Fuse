using Fuse.Plugins.Abstractions.Reducers;

namespace Fuse.Plugins.Languages.CSharp.Reducers;

/// <summary>
///     Detects EF Core migrations and model snapshots and collapses their generated method bodies.
/// </summary>
public sealed class GeneratedCodeDetector : IGeneratedCodeDetector
{
    /// <inheritdoc />
    public bool IsGenerated(string content) => GeneratedCodeCollapser.IsGenerated(content);

    /// <inheritdoc />
    public string Collapse(string content) => GeneratedCodeCollapser.Collapse(content);
}
