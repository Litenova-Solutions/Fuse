using Fuse.Collection.Models;

namespace Fuse.Collection.Templates;

/// <summary>
///     Discovers project templates from dependency injection and resolves them by enum value.
/// </summary>
public sealed class ProjectTemplateRegistry
{
    private readonly IReadOnlyDictionary<ProjectTemplate, IProjectTemplate> _templates;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ProjectTemplateRegistry" /> class.
    /// </summary>
    /// <param name="templates">All registered project template implementations.</param>
    public ProjectTemplateRegistry(IEnumerable<IProjectTemplate> templates)
    {
        _templates = templates.ToDictionary(
            template => Enum.Parse<ProjectTemplate>(template.Name),
            template => template);
    }

    /// <summary>
    ///     Gets the template configuration for the specified project template.
    /// </summary>
    /// <param name="template">The project template to retrieve.</param>
    /// <returns>
    ///     The matching template configuration, or the <see cref="ProjectTemplate.Generic" />
    ///     template when the requested template is not registered.
    /// </returns>
    public IProjectTemplate GetTemplate(ProjectTemplate template)
    {
        return _templates.TryGetValue(template, out var registeredTemplate)
            ? registeredTemplate
            : _templates[ProjectTemplate.Generic];
    }

    /// <summary>
    ///     Gets all registered project templates.
    /// </summary>
    /// <returns>A read-only collection of registered templates.</returns>
    public IReadOnlyCollection<IProjectTemplate> GetAllTemplates()
    {
        return _templates.Values.ToArray();
    }
}
