using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for F# projects.
/// </summary>
public sealed class FsharpTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Fsharp);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".fs", ".fsi", ".fsx", ".fsproj", ".config", ".sln"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["bin", "obj", ".vs", "packages", "node_modules", ".git"];
}
