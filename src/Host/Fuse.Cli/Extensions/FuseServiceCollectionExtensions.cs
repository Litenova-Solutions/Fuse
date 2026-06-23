using Fuse.Fusion.Embeddings.Onnx.Extensions;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Formats.Web.Extensions;
using Fuse.Plugins.Languages.CSharp.Extensions;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Extensions;

/// <summary>
///     Host composition root for registering the full Fuse stack used by the CLI and MCP server.
/// </summary>
public static class FuseServiceCollectionExtensions
{
    /// <summary>
    ///     Registers core fusion services, C# language and Roslyn structural plugins, format reducers, and
    ///     optionally the ONNX embedding backend.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="explicitEmbeddingsFlag">
    ///     The value of an explicit <c>--embeddings</c> flag from the CLI, or <see langword="null" /> when
    ///     absent. When <see langword="null" />, ONNX embeddings are enabled only when
    ///     <c>FUSE_EMBEDDINGS</c> is set; the MCP server passes <see langword="null" /> and relies on that
    ///     environment variable (see <see cref="OnnxEmbeddingsServiceCollectionExtensions.EnableVariable" />).
    /// </param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     This is the single composition root for production hosts. Callers should not register
    ///     <c>AddFuseCore</c>, <c>AddCSharpRoslyn</c>, or ONNX separately.
    /// </remarks>
    public static IServiceCollection AddFuse(this IServiceCollection services, bool? explicitEmbeddingsFlag = null)
    {
        services.AddFuseCore();
        services.AddCSharpLanguage();
        services.AddCSharpRoslyn();
        services.AddFormatReducers();
        services.AddFuseOnnxEmbeddings(explicitEmbeddingsFlag);
        return services;
    }
}
