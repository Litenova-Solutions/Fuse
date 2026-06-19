using Fuse.Collection.Models;

namespace Fuse.Collection.Templates.Definitions;

/// <summary>
///     Template for Clojure projects.
/// </summary>
public sealed class ClojureTemplate : ProjectTemplateBase
{
    /// <inheritdoc />
    public override string Name => nameof(ProjectTemplate.Clojure);

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Extensions =>
        [".clj", ".cljs", ".cljc", ".edn"];

    /// <inheritdoc />
    public override IReadOnlyCollection<string> ExcludeDirectories =>
        ["target", ".cpcache", ".git"];
}
