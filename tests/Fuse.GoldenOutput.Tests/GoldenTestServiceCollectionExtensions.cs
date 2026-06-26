using Fuse.Cli.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.GoldenOutput.Tests;

internal static class GoldenTestServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the production host composition root used by the CLI and MCP server.
    /// </summary>
    public static IServiceCollection AddFuseForTests(this IServiceCollection services) =>
        services.AddFuse();
}
