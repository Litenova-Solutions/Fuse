using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Elixir projects (Phoenix).
/// </summary>
public sealed class ElixirTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Elixir);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".ex", ".exs", ".eex", ".leex", "mix.exs"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["_build", "deps", ".git"];
}
