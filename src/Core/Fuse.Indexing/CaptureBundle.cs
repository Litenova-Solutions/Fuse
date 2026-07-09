using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Indexing;

/// <summary>
///     One project's summary in a capture-bundle manifest (C2): the shape a consumer reads to know what the bundle
///     rehydrates, without opening the compiler log.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="AssemblyName">The output assembly name.</param>
/// <param name="ErrorCount">The compile errors in the captured compilation (0 for a clean build-capture).</param>
/// <param name="TypeCount">The named types the compilation declares.</param>
/// <param name="SymbolCount">The extracted symbol records.</param>
/// <param name="NodeCount">The semantic-graph nodes.</param>
/// <param name="EdgeCount">The semantic-graph edges.</param>
public sealed record CaptureProjectEntry(
    string Name,
    string? AssemblyName,
    int ErrorCount,
    int TypeCount,
    int SymbolCount,
    int NodeCount,
    int EdgeCount);

/// <summary>
///     The manifest of a portable capture bundle (C2): the versioned metadata a consumer checks before rehydrating,
///     so a bundle produced by an incompatible Fuse build (or an unknown bundle format) is refused with an
///     actionable message rather than rehydrated into a wrong-shaped store. Stamped with the producing Fuse
///     version and the source commit, alongside the per-project summary.
/// </summary>
/// <param name="BundleFormatVersion">The bundle layout version (bumped when the on-disk bundle shape changes).</param>
/// <param name="FuseVersion">The Fuse version that produced the bundle (<see cref="FuseBuildInfo.Current" />).</param>
/// <param name="Commit">The source commit the capture was taken at, when known.</param>
/// <param name="CapturedUtc">The ISO-8601 UTC time the bundle was produced, when recorded.</param>
/// <param name="Projects">The per-project summary of what the bundle rehydrates.</param>
public sealed record CaptureManifest(
    int BundleFormatVersion,
    string FuseVersion,
    string? Commit,
    string? CapturedUtc,
    IReadOnlyList<CaptureProjectEntry> Projects)
{
    /// <summary>The current bundle layout version; a bundle with a different version is refused.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The manifest file name inside a bundle directory.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The portable compiler-log file name inside a bundle directory.</summary>
    public const string CompilerLogFileName = "capture.complog";

    /// <summary>The extracted-graph file name inside a bundle directory (a serialized <see cref="CaptureResult" />).</summary>
    public const string GraphFileName = "graph.json";

    /// <summary>
    ///     Whether this bundle can be rehydrated by the running Fuse build: the bundle format version matches and
    ///     the producing Fuse version is compatible by <c>major.minor</c> (<see cref="FuseBuildInfo.IsCompatible" />),
    ///     so the extraction contract has not changed under it.
    /// </summary>
    [JsonIgnore]
    public bool IsCompatibleWithRunningBuild =>
        BundleFormatVersion == CurrentFormatVersion && FuseBuildInfo.IsCompatible(FuseVersion);

    /// <summary>
    ///     The actionable reason a bundle is incompatible with the running build, or null when it is compatible.
    /// </summary>
    [JsonIgnore]
    public string? IncompatibilityReason
    {
        get
        {
            if (BundleFormatVersion != CurrentFormatVersion)
                return $"bundle format version {BundleFormatVersion} is not supported by this Fuse (expected {CurrentFormatVersion}); re-capture with this Fuse version.";
            if (!FuseBuildInfo.IsCompatible(FuseVersion))
                return $"bundle was produced by Fuse {FuseVersion}, incompatible with the running {FuseBuildInfo.Current} (major.minor differs); re-capture with the running version.";
            return null;
        }
    }
}

/// <summary>
///     Source-generated JSON context for the capture-bundle manifest, per the project invariant that JSON uses a
///     source-generated <see cref="JsonSerializerContext" /> rather than reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CaptureManifest))]
public sealed partial class CaptureManifestJsonContext : JsonSerializerContext;

/// <summary>
///     Reads and writes a capture bundle directory (C2): a folder containing the manifest, the portable compiler
///     log, and the extracted graph. Kept as plain file and JSON operations (no build dependency) so both the
///     producer (<c>fuse capture</c>) and the consumer (<c>fuse index --from-capture</c>) share one layout.
/// </summary>
public static class CaptureBundleIo
{
    /// <summary>The absolute path to a bundle's manifest file.</summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The manifest path.</returns>
    public static string ManifestPath(string bundleDirectory) => Path.Combine(bundleDirectory, CaptureManifest.ManifestFileName);

    /// <summary>The absolute path to a bundle's portable compiler log.</summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The compiler-log path.</returns>
    public static string CompilerLogPath(string bundleDirectory) => Path.Combine(bundleDirectory, CaptureManifest.CompilerLogFileName);

    /// <summary>The absolute path to a bundle's extracted-graph file.</summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The graph path.</returns>
    public static string GraphPath(string bundleDirectory) => Path.Combine(bundleDirectory, CaptureManifest.GraphFileName);

    /// <summary>
    ///     Writes a bundle directory from an already-produced compiler log and extracted graph: moves the compiler
    ///     log in, serializes the graph, and writes the manifest stamped with the running Fuse version.
    /// </summary>
    /// <param name="bundleDirectory">The output bundle directory (created if absent).</param>
    /// <param name="compilerLogPath">The portable compiler log the worker produced (moved into the bundle).</param>
    /// <param name="graph">The extracted graph the worker returned.</param>
    /// <param name="commit">The source commit, when known.</param>
    /// <param name="capturedUtc">The ISO-8601 UTC capture time, when recorded.</param>
    /// <returns>The manifest written into the bundle.</returns>
    public static CaptureManifest Write(
        string bundleDirectory, string compilerLogPath, CaptureResult graph, string? commit, string? capturedUtc)
    {
        Directory.CreateDirectory(bundleDirectory);

        var destComplog = CompilerLogPath(bundleDirectory);
        if (Path.GetFullPath(compilerLogPath) != Path.GetFullPath(destComplog))
        {
            if (File.Exists(destComplog))
                File.Delete(destComplog);
            File.Move(compilerLogPath, destComplog);
        }

        File.WriteAllText(GraphPath(bundleDirectory),
            JsonSerializer.Serialize(graph, BuildCaptureJsonContext.Default.CaptureResult));

        var manifest = new CaptureManifest(
            CaptureManifest.CurrentFormatVersion,
            FuseBuildInfo.Current,
            commit,
            capturedUtc,
            graph.Projects.Select(p => new CaptureProjectEntry(
                p.Name, p.AssemblyName, p.ErrorCount, p.TypeCount, p.SymbolCount, p.NodeCount, p.EdgeCount)).ToList());
        File.WriteAllText(ManifestPath(bundleDirectory), CaptureManifestJson.Serialize(manifest));
        return manifest;
    }

    /// <summary>
    ///     Reads a bundle's manifest, or null when the directory has no readable manifest.
    /// </summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The manifest, or null when absent or unparseable.</returns>
    public static CaptureManifest? ReadManifest(string bundleDirectory)
    {
        var path = ManifestPath(bundleDirectory);
        if (!File.Exists(path))
            return null;
        return CaptureManifestJson.Deserialize(File.ReadAllText(path));
    }

    /// <summary>
    ///     Reads a bundle's extracted graph, or null when absent or unparseable.
    /// </summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The extracted graph, or null.</returns>
    public static CaptureResult? ReadGraph(string bundleDirectory)
    {
        var path = GraphPath(bundleDirectory);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), BuildCaptureJsonContext.Default.CaptureResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     Serializes and deserializes a <see cref="CaptureManifest" />.
/// </summary>
public static class CaptureManifestJson
{
    /// <summary>Serializes a manifest to indented JSON.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON document.</returns>
    public static string Serialize(CaptureManifest manifest) =>
        JsonSerializer.Serialize(manifest, CaptureManifestJsonContext.Default.CaptureManifest);

    /// <summary>Deserializes a manifest from JSON, or null when the JSON is not a manifest.</summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The manifest, or null when it cannot be parsed.</returns>
    public static CaptureManifest? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CaptureManifestJsonContext.Default.CaptureManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
