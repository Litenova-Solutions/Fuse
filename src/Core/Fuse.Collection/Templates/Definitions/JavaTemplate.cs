using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Java projects (Maven, Gradle).
/// </summary>
public sealed class JavaTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Java);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".java", ".gradle", ".xml", ".properties", ".jar", ".jsp", ".jspx", ".class"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["build", "target", ".gradle", ".mvn", "node_modules", ".git"];
}
