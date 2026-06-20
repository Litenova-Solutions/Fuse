using Fuse.Plugins.Formats.Web.Reducers;
using Fuse.Plugins.Abstractions.Reducers;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Formats.Web.Extensions;

/// <summary>
///     Registers format-specific content reducers with dependency injection.
/// </summary>
public static class FormatReducersServiceCollectionExtensions
{
    /// <summary>
    ///     Registers every markup and configuration format <see cref="IContentReducer" /> (CSS, HTML,
    ///     JavaScript and TypeScript, JSON, Markdown, Razor, SQL, SCSS, XML, and YAML) as singletons.
    /// </summary>
    /// <param name="services">The service collection to add the reducers to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow call chaining.</returns>
    public static IServiceCollection AddFormatReducers(this IServiceCollection services)
    {
        services.AddSingleton<IContentReducer, CssReducer>();
        services.AddSingleton<IContentReducer, HtmlReducer>();
        services.AddSingleton<IContentReducer, JavaScriptReducer>();
        services.AddSingleton<IContentReducer, JsonReducer>();
        services.AddSingleton<IContentReducer, MarkdownReducer>();
        services.AddSingleton<IContentReducer, RazorReducer>();
        services.AddSingleton<IContentReducer, SqlReducer>();
        services.AddSingleton<IContentReducer, ScssReducer>();
        services.AddSingleton<IContentReducer, XmlReducer>();
        services.AddSingleton<IContentReducer, YamlReducer>();

        return services;
    }
}
