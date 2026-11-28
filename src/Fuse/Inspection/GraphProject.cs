namespace Fuse.Inspection;

/// <summary>One project in the repository graph and its reference edges.</summary>
/// <param name="Name">Project name without extension.</param>
/// <param name="Path">Repository-relative path to the project file.</param>
/// <param name="References">Names of projects this project references.</param>
/// <param name="ReferencedBy">Names of projects that reference this project.</param>
/// <param name="IsTestProject">True when the project is a test project.</param>
/// <param name="Files">Repository-relative paths of C# files compiled by this project.</param>
internal sealed record GraphProject(
    string Name,
    string Path,
    string[]? References = null,
    string[]? ReferencedBy = null,
    bool IsTestProject = false,
    string[]? Files = null)
{
    /// <summary>Names of projects this project references.</summary>
    public string[] References { get; init; } = References ?? [];

    /// <summary>Names of projects that reference this project.</summary>
    public string[] ReferencedBy { get; init; } = ReferencedBy ?? [];

    /// <summary>Repository-relative paths of C# files compiled by this project.</summary>
    public string[] Files { get; init; } = Files ?? [];
}
