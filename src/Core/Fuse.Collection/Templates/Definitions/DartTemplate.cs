using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Dart projects (Flutter).
/// </summary>
public sealed class DartTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Dart);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".dart", ".yaml", ".lock"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["build", ".dart_tool", ".pub-cache", ".git"];
}
