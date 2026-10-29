namespace Fuse.Dotnet;

/// <summary>
///     One error of a build's console output, in first-seen order. A file diagnostic names its file, line and column; a
///     general error, such as <c>CSC : error CS5001</c>, names only where it came from.
/// </summary>
/// <param name="File">Repository-relative path with forward slashes, or the origin text of a general error.</param>
/// <param name="Line">One-based line, or null for a general error.</param>
/// <param name="Column">One-based column, or null for a general error.</param>
/// <param name="Code">The diagnostic id, for example <c>CS1061</c>.</param>
/// <param name="Message">The message, in the invariant culture.</param>
internal sealed record BuildDiagnostic(string File, int? Line, int? Column, string Code, string Message)
{
    /// <summary>The MSBuild canonical form, which coding agents and editors already parse.</summary>
    public override string ToString() =>
        Line is null ? $"{File}: error {Code}: {Message}" : $"{File}({Line},{Column}): error {Code}: {Message}";
}
