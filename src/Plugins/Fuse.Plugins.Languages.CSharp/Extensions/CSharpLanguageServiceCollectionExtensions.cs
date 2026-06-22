using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Maps;
using Fuse.Plugins.Abstractions.Patterns;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Dependencies;
using Fuse.Plugins.Languages.CSharp.Maps;
using Fuse.Plugins.Languages.CSharp.Markers;
using Fuse.Plugins.Languages.CSharp.Outline;
using Fuse.Plugins.Languages.CSharp.Patterns;
using Fuse.Plugins.Languages.CSharp.Reducers;
using Fuse.Plugins.Languages.CSharp.Skeleton;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Languages.CSharp.Extensions;

/// <summary>
///     Registers C# language capabilities with dependency injection.
/// </summary>
public static class CSharpLanguageServiceCollectionExtensions
{
    /// <summary>
    ///     Registers every C# language capability (skeleton extractor, semantic marker generator, dependency
    ///     extractor, type-name locator, content reducer, route and project-graph generators, and the pattern
    ///     detectors) as singletons.
    /// </summary>
    /// <param name="services">The service collection to add the capabilities to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow call chaining.</returns>
    /// <remarks>
    ///     Each pattern detector is registered once as its concrete type and then exposed through both the
    ///     <see cref="IPatternDetector" /> and <see cref="PatternDetectorBase" /> contracts so a single shared
    ///     instance backs every resolution.
    /// </remarks>
    public static IServiceCollection AddCSharpLanguage(this IServiceCollection services)
    {
        services.AddSingleton<ISkeletonExtractor, CSharpSkeletonExtractor>();
        services.AddSingleton<ISymbolOutlineExtractor, CSharpOutlineExtractor>();
        services.AddSingleton<ISymbolChunkExtractor, CSharpSymbolChunkExtractor>();
        services.AddSingleton<ISemanticMarkerGenerator, CSharpSemanticMarkerGenerator>();
        services.AddSingleton<IDependencyExtractor, CSharpDependencyExtractor>();
        services.AddSingleton<ITypeNameLocator, CSharpTypeNameLocator>();
        services.AddSingleton<IContentReducer, CSharpReducer>();
        services.AddSingleton<IRouteMapGenerator, CSharpRouteMapGenerator>();
        services.AddSingleton<IProjectGraphGenerator, CSharpProjectGraphGenerator>();

        services.AddSingleton<DiRegistrationPatternDetector>();
        services.AddSingleton<ExceptionHandlingPatternDetector>();
        services.AddSingleton<LoggingPatternDetector>();
        services.AddSingleton<AsyncPatternDetector>();
        services.AddSingleton<CqrsPatternDetector>();
        services.AddSingleton<RepositoryPatternDetector>();

        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<DiRegistrationPatternDetector>());
        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<ExceptionHandlingPatternDetector>());
        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<LoggingPatternDetector>());
        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<AsyncPatternDetector>());
        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<CqrsPatternDetector>());
        services.AddSingleton<IPatternDetector>(sp => sp.GetRequiredService<RepositoryPatternDetector>());

        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<DiRegistrationPatternDetector>());
        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<ExceptionHandlingPatternDetector>());
        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<LoggingPatternDetector>());
        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<AsyncPatternDetector>());
        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<CqrsPatternDetector>());
        services.AddSingleton<PatternDetectorBase>(sp => sp.GetRequiredService<RepositoryPatternDetector>());

        return services;
    }
}
