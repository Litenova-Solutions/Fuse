using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for JavaScript projects.
/// </summary>
public sealed class JavaScriptTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.JavaScript);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".js", ".jsx", ".json", ".ts", ".tsx", ".html", ".css", ".scss", ".less", ".mjs"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"];
}
