using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Visual Basic .NET projects.
/// </summary>
public sealed class VbNetTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.VbNet);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".vb", ".vbproj", ".config", ".settings", ".resx", ".sln"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["bin", "obj", ".vs", "packages", "node_modules", ".git"];
}
