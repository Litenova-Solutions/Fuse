using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Haskell projects.
/// </summary>
public sealed class HaskellTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Haskell);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".hs", ".lhs", ".cabal", ".hs-boot"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["dist", "dist-newstyle", ".stack-work", ".git"];
}
