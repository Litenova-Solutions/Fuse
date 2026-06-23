using Fuse.Fusion.Extensions;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

internal static class FuseTestServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the default fusion stack plus the Roslyn C# structural tier, matching production hosts.
    /// </summary>
    public static IServiceCollection AddFuseForTests(this IServiceCollection services)
    {
        services.AddFuse();
        services.AddCSharpRoslyn();
        return services;
    }
}
