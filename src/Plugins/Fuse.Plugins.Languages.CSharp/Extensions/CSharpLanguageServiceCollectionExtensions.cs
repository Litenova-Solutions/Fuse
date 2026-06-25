using Fuse.Plugins.Abstractions.Maps;
using Fuse.Plugins.Abstractions.Patterns;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Languages.CSharp.Maps;
using Fuse.Plugins.Languages.CSharp.Patterns;
using Fuse.Plugins.Languages.CSharp.Reducers;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Languages.CSharp.Extensions;

/// <summary>
///     Registers C# language capabilities with dependency injection.
/// </summary>
public static class CSharpLanguageServiceCollectionExtensions
{
    /// <summary>
    ///     Registers every C# language capability (content reducer, project-graph generator, and the pattern
    ///     detectors) as singletons. Structural capabilities (skeleton, outline, dependency, type-name, route
    ///     map, semantic markers) are registered by the Roslyn plugin.
    /// </summary>
    /// <param name="services">The service collection to add the capabilities to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow call chaining.</returns>
    /// <remarks>
    ///     Pattern detectors are registered as transient because each detection run mutates per-detector
    ///     accumulation state. A fresh instance per resolution lets <see cref="PatternDetectorBase" /> consumers
    ///     run a private batch without racing concurrent runs over shared state (see the detector factory wired
    ///     into the post-reduction pipeline). Each detector is registered as
    ///     <see cref="PatternDetectorBase" />; consumers resolve via
    ///     <c>GetServices&lt;PatternDetectorBase&gt;()</c>.
    /// </remarks>
    public static IServiceCollection AddCSharpLanguage(this IServiceCollection services)
    {
        services.AddSingleton<IContentReducer, CSharpReducer>();
        services.AddSingleton<IGeneratedCodeDetector, GeneratedCodeDetector>();
        services.AddSingleton<IProjectGraphGenerator, CSharpProjectGraphGenerator>();

        services.AddTransient<PatternDetectorBase, DiRegistrationPatternDetector>();
        services.AddTransient<PatternDetectorBase, ExceptionHandlingPatternDetector>();
        services.AddTransient<PatternDetectorBase, LoggingPatternDetector>();
        services.AddTransient<PatternDetectorBase, AsyncPatternDetector>();
        services.AddTransient<PatternDetectorBase, CqrsPatternDetector>();
        services.AddTransient<PatternDetectorBase, RepositoryPatternDetector>();

        return services;
    }
}
