using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for PHP projects.
/// </summary>
public sealed class PhpTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Php);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".php", ".phtml", ".php7", ".phps", ".php-s", ".pht", ".phar"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["vendor", "node_modules", ".git"];
}
