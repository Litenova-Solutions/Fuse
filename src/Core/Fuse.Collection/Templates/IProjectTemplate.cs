namespace Fuse.Collection.Templates;

/// <summary>
///     Defines the contract for a project template configuration.
/// </summary>
/// <remarks>
///     Project templates provide predefined settings for file extensions, excluded directories,
///     and excluded file patterns based on the type of project being processed.
/// </remarks>
public interface IProjectTemplate
{
    /// <summary>
    ///     Gets the template name, matching a <see cref="Models.ProjectTemplate" /> enum value.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Gets the file extensions to include for this template.
    /// </summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>
    ///     Gets the directory names to exclude for this template.
    /// </summary>
    IReadOnlyCollection<string> ExcludeDirectories { get; }

    /// <summary>
    ///     Gets the glob patterns to exclude for this template.
    /// </summary>
    IReadOnlyCollection<string> ExcludePatterns { get; }
}
