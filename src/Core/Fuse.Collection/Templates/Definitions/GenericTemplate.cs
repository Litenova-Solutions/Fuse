using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for general text-based files with minimal assumptions.
/// </summary>
public sealed class GenericTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Generic);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".txt", ".md", ".json", ".xml", ".yaml", ".yml"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        [".git", ".svn", ".hg", "node_modules", ".vscode", ".idea"];
}
