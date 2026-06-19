using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Scala projects.
/// </summary>
public sealed class ScalaTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Scala);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".scala", ".sbt", ".sc"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["target", "project/target", ".bloop", ".metals", ".git"];
}
