using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Erlang projects.
/// </summary>
public sealed class ErlangTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Erlang);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".erl", ".hrl", ".app.src", "rebar.config"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["_build", ".rebar3", ".git"];
}
