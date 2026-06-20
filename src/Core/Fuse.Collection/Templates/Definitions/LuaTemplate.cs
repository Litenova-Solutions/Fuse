using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Lua projects.
/// </summary>
public sealed class LuaTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Lua);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".lua", ".rockspec"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        [".git"];
}
