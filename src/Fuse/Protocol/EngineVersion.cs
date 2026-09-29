using System.Reflection;

namespace Fuse.Protocol;

/// <summary>
///     Identifies a build of Fuse. Client and engine must match exactly; the module id changes with every code change.
///     It belongs to the protocol because every request carries <see cref="Build"/>, and an engine from another build
///     answers <see cref="EngineResponse.Restart"/> instead of reading the rest of the request.
/// </summary>
internal static class EngineVersion
{
    /// <summary>The product version, as <c>fuse --version</c> prints it and the MCP server reports it.</summary>
    public static string Product { get; } =
        typeof(EngineVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The product version and the module id of this build, which a request carries as its <see cref="EngineRequest.BuildId"/>.</summary>
    public static string Build { get; } = $"{Product}/{typeof(EngineVersion).Assembly.ManifestModule.ModuleVersionId:N}";
}
