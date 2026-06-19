using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Ruby projects (Rails, gems).
/// </summary>
public sealed class RubyTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Ruby);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".rb", ".rake", ".gemspec", "Gemfile", "Rakefile", ".erb", ".haml", ".slim"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["vendor", ".bundle", "coverage", "tmp", "log", ".git"];
}
