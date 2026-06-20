using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Perl projects.
/// </summary>
public sealed class PerlTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Perl);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".pl", ".pm", ".t"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["blib", "_build", ".git"];
}
