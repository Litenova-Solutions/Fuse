namespace Fuse.Protocol;

/// <summary>The errors introduced by the working-tree changes, and how far the check reached.</summary>
/// <param name="Introduced">Working-tree errors absent at HEAD, ordered by file and position.</param>
/// <param name="FilesChecked">Number of target and candidate files the check examined.</param>
/// <param name="Projects">Names of the projects the introduced errors are in.</param>
/// <param name="SurfaceChangedIn">Projects whose declarations changed, which is what triggers checking dependents.</param>
/// <param name="DependentProjectsChecked">Number of dependent projects searched for breaks.</param>
/// <param name="WholeProjects">True when the candidate count exceeds the threshold and whole projects are bound.</param>
/// <param name="Context">One line per introduced error naming the changed declaration that put the error's file in scope, or null for an error that gets none. Same order and length as <paramref name="Introduced"/>.</param>
/// <param name="ContextLeftOut">How many errors had a line that the cap left out.</param>
internal sealed record CheckReport(
    Diagnostic[] Introduced,
    int FilesChecked,
    string[] Projects,
    string[] SurfaceChangedIn,
    int DependentProjectsChecked,
    bool WholeProjects,
    string?[]? Context = null,
    int ContextLeftOut = 0)
{
    /// <summary>The line for the error at <paramref name="index"/>, or null when it has none.</summary>
    public string? ContextFor(int index) =>
        Context is not null && index >= 0 && index < Context.Length ? Context[index] : null;
}

/// <summary>One compiler or analyzer error.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Column">One-based column.</param>
/// <param name="Id">Diagnostic id, for example <c>CS1061</c>.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Analyzer">True when an analyzer reported it rather than the compiler.</param>
internal sealed record Diagnostic(string Path, int Line, int Column, string Id, string Message, bool Analyzer = false)
{
    /// <summary>The MSBuild canonical form, which models already parse.</summary>
    public override string ToString() => $"{Path}({Line},{Column}): error {Id}: {Message}";
}
