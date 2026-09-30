using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fuse.Protocol;

/// <summary>
///     Identifies a build of Fuse. Client and engine must match exactly; the module id changes with every code change.
///     It belongs to the protocol because every request carries <see cref="Build"/>, and an engine from another build
///     answers <see cref="EngineResponse.Restart"/> instead of reading the rest of the request.
/// </summary>
internal static class EngineVersion
{
    /// <summary>The managed assembly the engine always runs from, on the shared framework.</summary>
    public const string ManagedAssembly = "fuse.dll";

    /// <summary>The product version, as <c>fuse --version</c> prints it and the MCP server reports it.</summary>
    public static string Product { get; } =
        typeof(EngineVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    ///     True in the native client, the build of the client compiled ahead of time to native code. It cannot run the
    ///     engine, which loads MSBuild and analyzers at run time, so it starts <see cref="ManagedAssembly"/> instead.
    /// </summary>
    public static bool IsNativeClient => !RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    ///     The directory holding the <see cref="ManagedAssembly"/> this build runs the engine from, or null when the native
    ///     client has none.
    /// </summary>
    public static string? ManagedDirectory { get; } = FindManagedDirectory();

    /// <summary>
    ///     The product version and the module id of <see cref="ManagedAssembly"/>, which a request carries as its
    ///     <see cref="EngineRequest.BuildId"/>. The native client is a separate compilation of the same sources with a
    ///     module id of its own, so it reports the id of the <see cref="ManagedAssembly"/> packed with it, which is the
    ///     engine it starts.
    /// </summary>
    public static string Build { get; } = $"{Product}/{BuildModuleId():N}";

    private static string? FindManagedDirectory() =>
        FindManagedDirectory(AppContext.BaseDirectory, IsNativeClient, RuntimeInformation.RuntimeIdentifier, Product);

    /// <summary>
    ///     Where <see cref="ManagedDirectory"/> is for a client running from <paramref name="baseDirectory"/>. The native
    ///     client finds <see cref="ManagedAssembly"/> beside it in a tool package, and through the tools directory's
    ///     .store folder on Windows, where dotnet tool install copies the native client alone into the tools directory as
    ///     the fuse command and unpacks the package under .store/fuse/&lt;version&gt;/.
    /// </summary>
    internal static string? FindManagedDirectory(string baseDirectory, bool isNativeClient, string runtimeIdentifier, string product)
    {
        if (!isNativeClient || File.Exists(Path.Combine(baseDirectory, ManagedAssembly)))
            return baseDirectory;
        var store = Path.Combine(baseDirectory, ".store", "fuse", product);
        // The RID-specific package is Litenova.Fuse.<RID> (RidPackageIdPrefix in Fuse.csproj), which the store names in lower case.
        var packaged = Path.Combine(store, $"litenova.fuse.{runtimeIdentifier}", product, "tools", "net10.0", runtimeIdentifier);
        if (File.Exists(Path.Combine(packaged, ManagedAssembly)))
            return packaged;
        return Directory.Exists(store)
            ? Directory.EnumerateFiles(store, ManagedAssembly, SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(Path.GetDirectoryName).FirstOrDefault()
            : null;
    }

    /// <remarks>A native client without the managed assembly reports its own id; it cannot start an engine, and EngineLauncher says so.</remarks>
    private static Guid BuildModuleId() =>
        IsNativeClient && ManagedDirectory is { } directory
            ? ModuleIdOf(Path.Combine(directory, ManagedAssembly))
            : typeof(EngineVersion).Assembly.ManifestModule.ModuleVersionId;

    /// <summary>The module id of the assembly at <paramref name="path"/>, read from its metadata without loading it.</summary>
    internal static Guid ModuleIdOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}
