using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for TypeScript projects.
/// </summary>
public sealed class TypeScriptTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.TypeScript);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".ts", ".tsx", ".js", ".jsx", ".json", ".html", ".css", ".scss", ".less"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["node_modules", "dist", "build", "coverage", ".next", ".nuxt", ".git"];
}
