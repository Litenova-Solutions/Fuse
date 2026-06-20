using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Azure DevOps wiki repositories.
/// </summary>
public sealed class AzureDevOpsWikiTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.AzureDevOpsWiki);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".md"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        [".git", ".attachments"];
}
