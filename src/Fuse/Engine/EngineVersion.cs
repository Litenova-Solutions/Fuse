using System.Reflection;

namespace Fuse.Engine;

/// <summary>Identifies a build of Fuse. Client and engine must match exactly; the module id changes with every code change.</summary>
internal static class EngineVersion
{
    public static string Product { get; } =
        typeof(EngineVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string Build { get; } = $"{Product}/{typeof(EngineVersion).Assembly.ManifestModule.ModuleVersionId:N}";
}
