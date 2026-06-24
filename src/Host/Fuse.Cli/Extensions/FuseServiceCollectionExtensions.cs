using Fuse.Fusion.Extensions;
using Fuse.Plugins.Formats.Web.Extensions;
using Fuse.Plugins.Languages.CSharp.Extensions;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Fuse.Plugins.Rerank.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Extensions;

/// <summary>
///     Host composition root for registering the full Fuse stack used by the CLI and MCP server.
/// </summary>
public static class FuseServiceCollectionExtensions
{
    /// <summary>
    ///     Registers core fusion services, C# language and Roslyn structural plugins, and format reducers.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     This is the single composition root for production hosts. Callers should not register
    ///     <c>AddFuseCore</c> or <c>AddCSharpRoslyn</c> separately.
    /// </remarks>
    public static IServiceCollection AddFuse(this IServiceCollection services)
    {
        services.AddFuseCore();
        services.AddCSharpLanguage();
        services.AddCSharpRoslyn();
        services.AddFormatReducers();
        // Registers the dense reranker only when its model is cached; absent a model the query path stays
        // lexical, so the no-model floor is preserved.
        services.AddOnnxDenseReranker();
        return services;
    }
}
