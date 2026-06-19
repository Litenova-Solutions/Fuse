using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for R projects (statistics, data science).
/// </summary>
public sealed class RTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.R);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".R", ".Rmd", ".Rproj", ".RData", ".rds"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        [".Rproj.user", ".Rhistory", ".RData", ".Ruserdata", ".git"];
}
