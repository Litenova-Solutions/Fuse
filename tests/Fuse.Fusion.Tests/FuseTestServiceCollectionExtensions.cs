using Fuse.Cli.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

internal static class FuseTestServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the production host composition root used by the CLI and MCP server.
    /// </summary>
    public static IServiceCollection AddFuseForTests(this IServiceCollection services) =>
        services.AddFuse();
}
