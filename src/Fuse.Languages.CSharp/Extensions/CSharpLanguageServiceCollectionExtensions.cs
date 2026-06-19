using Fuse.Languages.Abstractions.Dependencies;
using Fuse.Languages.Abstractions.Markers;
using Fuse.Languages.Abstractions.Maps;
using Fuse.Languages.Abstractions.Patterns;
using Fuse.Languages.Abstractions.Reducers;
using Fuse.Languages.Abstractions.Skeleton;
using Fuse.Languages.CSharp.Dependencies;
using Fuse.Languages.CSharp.Maps;
using Fuse.Languages.CSharp.Markers;
using Fuse.Languages.CSharp.Patterns;
using Fuse.Languages.CSharp.Reducers;
using Fuse.Languages.CSharp.Skeleton;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Languages.CSharp.Extensions;

/// <summary>
///     Registers C# language capabilities with dependency injection.
/// </summary>
public static class CSharpLanguageServiceCollectionExtensions
{
    /// <summary>
    ///     Adds C# skeleton, marker, dependency, pattern, and reducer capabilities.
    /// </summary>
    public static IServiceCollection AddCSharpLanguage(this IServiceCollection services)
    {
        services.AddSingleton<ISkeletonExtractor, CSharpSkeletonExtractor>();
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
