using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Rust projects (Cargo).
/// </summary>
public sealed class RustTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Rust);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".rs", ".toml", ".lock"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["target", ".cargo", ".git"];
}
