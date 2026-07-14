using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;

/// <summary>
///     Registers the C# semantic-tier language provider and the shared semantic provider registry.
/// </summary>
public static class CSharpSemanticProviderServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="CSharpSemanticLanguageProvider" /> and builds a
    ///     <see cref="SemanticLanguageProviderRegistry" /> from every registered
    ///     <see cref="ISemanticLanguageProvider" />.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow call chaining.</returns>
    /// <remarks>
    ///     Call after <c>AddCSharpRoslyn</c> (typically via <c>AddFuse</c>). Additional providers register
    ///     as <see cref="ISemanticLanguageProvider" /> before this call to join the registry; the C# provider
    ///     is always registered here.
    /// </remarks>
    public static IServiceCollection AddCSharpSemanticLanguageProvider(this IServiceCollection services)
    {
        services.AddSingleton<ISemanticLanguageProvider, CSharpSemanticLanguageProvider>();
        services.AddSingleton<SemanticLanguageProviderRegistry>(sp =>
            new SemanticLanguageProviderRegistry(sp.GetServices<ISemanticLanguageProvider>()));
        services.AddSingleton(sp => sp.GetRequiredService<SemanticLanguageProviderRegistry>().RunnerFor("csharp"));
        return services;
    }
}
