namespace Fuse.Plugins.Abstractions.Reducers;

/// <summary>
///     Detects machine-generated C# (for example EF Core migrations and model snapshots) and optionally
///     collapses generated method bodies to signatures.
/// </summary>
/// <remarks>
///     Host and reduction paths resolve this capability through dependency injection so callers outside the
///     C# plugin do not reference the collapser implementation directly.
/// </remarks>
public interface IGeneratedCodeDetector
{
    /// <summary>
    ///     Returns whether the content looks like machine-generated C# that is worth flagging or collapsing.
    /// </summary>
    /// <param name="content">The C# source to inspect.</param>
    /// <returns><see langword="true" /> when the file appears to be generated.</returns>
    bool IsGenerated(string content);

    /// <summary>
    ///     Collapses generated method bodies when the content is recognized as generated; otherwise returns
    ///     the content unchanged.
    /// </summary>
    /// <param name="content">The C# source to collapse.</param>
    /// <returns>The collapsed content, or the original when it is not recognized as generated.</returns>
    string Collapse(string content);
}
