using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Skeleton;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;

/// <summary>
///     Registers the opt-in Roslyn precision tier for C#.
/// </summary>
public static class RoslynServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Roslyn implementations of the C# skeleton, dependency, type-name, and outline capabilities.
    /// </summary>
    /// <param name="services">The service collection to add the capabilities to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     Call this <em>after</em> the regex C# plugin is registered (after <c>AddFuse</c>). Each capability
    ///     registry resolves an extension to its last registration, so registering the Roslyn implementations
    ///     last makes them win for <c>.cs</c> while the regex implementations remain the fallback for any
    ///     capability the Roslyn tier does not provide. This method is invoked only when semantic analysis is
    ///     requested, so the regex tier stays the default; it is never reached in the Native AOT build, which
    ///     does not reference this assembly.
    /// </remarks>
    public static IServiceCollection AddCSharpRoslyn(this IServiceCollection services)
    {
        services.AddSingleton<ISkeletonExtractor, RoslynSkeletonExtractor>();
        services.AddSingleton<IDependencyExtractor, RoslynDependencyExtractor>();
        services.AddSingleton<ITypeNameLocator, RoslynTypeNameLocator>();
        services.AddSingleton<ISymbolOutlineExtractor, RoslynOutlineExtractor>();
        return services;
    }
}
