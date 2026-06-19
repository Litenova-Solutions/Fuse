using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Swift projects (iOS, macOS).
/// </summary>
public sealed class SwiftTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Swift);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".swift", ".xib", ".storyboard", ".xcodeproj", ".pbxproj", ".plist"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        [".build", "Pods", ".git"];
}
