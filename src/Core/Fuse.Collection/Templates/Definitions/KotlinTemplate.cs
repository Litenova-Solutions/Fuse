using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Kotlin projects (Android, JVM).
/// </summary>
public sealed class KotlinTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Kotlin);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".kt", ".kts", ".java", ".xml", ".gradle"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["build", ".gradle", ".idea", ".git"];
}
