using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Maps;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Roslyn.Maps;
using Fuse.Plugins.Languages.CSharp.Roslyn.Markers;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;

/// <summary>
///     Registers the Roslyn structural tier for C#.
/// </summary>
public static class RoslynServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Roslyn implementations of the C# skeleton, dependency, type-name, outline, slice, chunk,
    ///     route-map, and semantic-marker capabilities.
    /// </summary>
    /// <param name="services">The service collection to add the capabilities to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow call chaining.</returns>
    /// <remarks>
    ///     Call this after <c>AddCSharpLanguage</c> (typically via <c>AddFuse</c>) so Roslyn supplies
    ///     every structural capability for <c>.cs</c>.
    /// </remarks>
    public static IServiceCollection AddCSharpRoslyn(this IServiceCollection services)
    {
        services.AddSingleton<ISkeletonExtractor, RoslynSkeletonExtractor>();
        services.AddSingleton<IDependencyExtractor, RoslynDependencyExtractor>();
        services.AddSingleton<ITypeNameLocator, RoslynTypeNameLocator>();
        services.AddSingleton<ISymbolOutlineExtractor, RoslynOutlineExtractor>();
        services.AddSingleton<ISymbolSliceExtractor, RoslynSymbolSliceExtractor>();
        services.AddSingleton<ISymbolChunkExtractor, RoslynSymbolChunkExtractor>();
        services.AddSingleton<IRouteMapGenerator, RoslynRouteMapGenerator>();
        services.AddSingleton<ISemanticMarkerGenerator, RoslynSemanticMarkerGenerator>();
        return services;
    }
}
