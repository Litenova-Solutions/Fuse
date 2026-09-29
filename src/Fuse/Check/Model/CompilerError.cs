namespace Fuse.Check.Model;

/// <summary>
///     One error the compiler or an analyzer reports, at error severity under the project's configuration. The wire
///     carries it unchanged, which is why <c>Fuse.Protocol</c> may use this one model type.
/// </summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Column">One-based column.</param>
/// <param name="Id">The diagnostic id, for example <c>CS1061</c>.</param>
/// <param name="Message">The message, in the invariant culture.</param>
/// <param name="FromAnalyzer">
///     True when an analyzer reported it, whether its file was bound on its own or with its whole project.
/// </param>
internal sealed record CompilerError(string Path, int Line, int Column, string Id, string Message, bool FromAnalyzer = false)
{
    /// <summary>The MSBuild canonical form, which coding agents and editors already parse.</summary>
    public override string ToString() => $"{Path}({Line},{Column}): error {Id}: {Message}";
}
