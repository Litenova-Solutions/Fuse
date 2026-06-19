using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Go projects.
/// </summary>
public sealed class GoTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Go);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".go", ".mod", ".sum"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["vendor", "bin", ".git"];
}
