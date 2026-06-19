using Fuse.Formats.Reducers;
using Fuse.Languages.Abstractions.Reducers;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Formats.Extensions;

/// <summary>
///     Registers format-specific content reducers with dependency injection.
/// </summary>
public static class FormatReducersServiceCollectionExtensions
{
    /// <summary>
    ///     Adds markup and config format reducers.
    /// </summary>
    public static IServiceCollection AddFormatReducers(this IServiceCollection services)
    {
        services.AddSingleton<IContentReducer, CssReducer>();
        services.AddSingleton<IContentReducer, HtmlReducer>();
        services.AddSingleton<IContentReducer, JavaScriptReducer>();
        services.AddSingleton<IContentReducer, JsonReducer>();
        services.AddSingleton<IContentReducer, MarkdownReducer>();
        services.AddSingleton<IContentReducer, RazorReducer>();
        services.AddSingleton<IContentReducer, ScssReducer>();
        services.AddSingleton<IContentReducer, XmlReducer>();
        services.AddSingleton<IContentReducer, YamlReducer>();

        return services;
    }
}
