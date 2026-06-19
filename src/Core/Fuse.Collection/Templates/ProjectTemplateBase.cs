namespace Fuse.Collection.Templates;

/// <summary>
///     Provides a base implementation for project templates with common functionality.
/// </summary>
/// <remarks>
///     Derived classes must implement <see cref="Name" />, <see cref="Extensions" />,
///     and <see cref="ExcludeDirectories" />. <see cref="ExcludePatterns" /> returns
///     an empty collection by default.
/// </remarks>
public abstract class ProjectTemplateBase : IProjectTemplate
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract IReadOnlyCollection<string> Extensions { get; }

    /// <inheritdoc />
    public abstract IReadOnlyCollection<string> ExcludeDirectories { get; }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<string> ExcludePatterns => [];
}
