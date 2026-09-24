namespace Fuse.Protocol;

/// <summary>The errors introduced by the working-tree changes, and how far the check reached.</summary>
/// <param name="Introduced">Errors present now and absent at HEAD, ordered by file and position.</param>
/// <param name="FilesChecked">How many documents were bound.</param>
/// <param name="Projects">Names of the projects the introduced errors are in.</param>
/// <param name="SurfaceChangedIn">Projects whose declarations changed, which is what triggers checking dependents.</param>
/// <param name="DependentProjectsChecked">How many dependent projects were searched for breaks.</param>
/// <param name="WholeProjects">True when the candidate set was too large and whole projects were bound instead.</param>
internal sealed record CheckReport(
    Diagnostic[] Introduced,
    int FilesChecked,
    string[] Projects,
    string[] SurfaceChangedIn,
    int DependentProjectsChecked,
    bool WholeProjects);

/// <summary>One compiler or analyzer error.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Column">One-based column.</param>
/// <param name="Id">Diagnostic id, for example <c>CS1061</c>.</param>
/// <param name="Message">The diagnostic message.</param>
internal sealed record Diagnostic(string Path, int Line, int Column, string Id, string Message)
{
    /// <summary>The MSBuild canonical form, which models already parse.</summary>
    public override string ToString() => $"{Path}({Line},{Column}): error {Id}: {Message}";
}
