namespace Fuse.Dotnet;

/// <summary>One warning of a build's file log, in first-seen order.</summary>
/// <param name="File">Repository-relative path with forward slashes.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Column">One-based column.</param>
/// <param name="Code">The diagnostic id, for example <c>CS0618</c>.</param>
/// <param name="Message">The message, in the invariant culture.</param>
internal sealed record BuildWarning(string File, int Line, int Column, string Code, string Message)
{
    /// <summary>The MSBuild canonical form, which coding agents and editors already parse.</summary>
    public override string ToString() => $"{File}({Line},{Column}): warning {Code}: {Message}";
}
