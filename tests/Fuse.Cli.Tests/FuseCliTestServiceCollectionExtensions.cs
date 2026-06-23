using Fuse.Cli.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

internal static class FuseCliTestServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the production host composition root used by the CLI and MCP server.
    /// </summary>
    public static IServiceCollection AddFuseForTests(this IServiceCollection services) =>
        services.AddFuse();
}
