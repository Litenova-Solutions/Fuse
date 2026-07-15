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
    /// <summary>
    ///     The current bundle layout version. Version 2 (G4) added an additive <c>fragments/</c> folder of
    ///     per-project compiler logs beside the version-1 single <c>capture.complog</c>; a version-2 reader still
    ///     reads a version-1 bundle. Version 3 (R60) adds target-framework information to the captured graph so a
    ///     consumer can build the canonical multi-target union. Version 4 adds cross-project <c>tests</c> edges to
    ///     the captured graph, so older bundles must be re-captured rather than silently omitting coverage.
    /// </summary>
    public const int CurrentFormatVersion = 4;

    /// <summary>The manifest file name inside a bundle directory.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The portable compiler-log file name inside a bundle directory (the version-1 single-log layout).</summary>
    public const string CompilerLogFileName = "capture.complog";

    /// <summary>The folder holding per-project compiler logs in a merged (version-2, G4) bundle.</summary>
    public const string FragmentsDirName = "fragments";

    /// <summary>The extracted-graph file name inside a bundle directory (a serialized <see cref="CaptureResult" />).</summary>
    public const string GraphFileName = "graph.json";

    /// <summary>
    ///     Whether this bundle can be rehydrated by the running Fuse build: its layout must exactly match the
    ///     current graph contract, because R60 needs target-framework availability and coverage edges from every
    ///     captured project.
    ///     The producing Fuse version must also be compatible by <c>major.minor</c>
    ///     (<see cref="FuseBuildInfo.IsCompatible" />), so the extraction contract has not changed under it.
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
            if (BundleFormatVersion > CurrentFormatVersion)
                return $"bundle format version {BundleFormatVersion} is newer than this Fuse understands (supports up to {CurrentFormatVersion}); upgrade Fuse or re-capture with this version.";
            if (BundleFormatVersion < CurrentFormatVersion)
                return $"bundle format version {BundleFormatVersion} lacks the current captured graph layout (requires {CurrentFormatVersion}); re-capture with the running version.";
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

    /// <summary>The absolute path to a bundle's per-project fragments folder (version-2 merged bundle).</summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The fragments folder path.</returns>
    public static string FragmentsDir(string bundleDirectory) => Path.Combine(bundleDirectory, CaptureManifest.FragmentsDirName);

    /// <summary>
    ///     The compiler logs a bundle carries, for the oracle check (G4): the per-project logs under
    ///     <c>fragments/</c> for a merged (version-2) bundle, or the single <c>capture.complog</c> for a direct
    ///     (version-1) bundle. A consumer iterates these to find the one carrying the file it is checking.
    /// </summary>
    /// <param name="bundleDirectory">The bundle directory.</param>
    /// <returns>The compiler-log paths, newest layout preferred; empty when the bundle carries no compiler log.</returns>
    public static IReadOnlyList<string> CompilerLogPaths(string bundleDirectory)
    {
        var fragments = FragmentsDir(bundleDirectory);
        if (Directory.Exists(fragments))
        {
            var logs = Directory.GetFiles(fragments, "*.complog").OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (logs.Count > 0)
                return logs;
        }

        var single = CompilerLogPath(bundleDirectory);
        return File.Exists(single) ? [single] : [];
    }

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
    ///     Writes a merged (version-2, G4) bundle from per-project compiler-log fragments and a merged graph:
    ///     moves each fragment log into the <c>fragments/</c> folder, serializes the graph, and writes the
    ///     manifest. Used by <c>fuse capture --merge</c> to assemble a bundle from per-project fragments a build
    ///     target emitted, equal in graph to a direct capture.
    /// </summary>
    /// <param name="bundleDirectory">The output bundle directory (created if absent).</param>
    /// <param name="fragmentLogPaths">The per-project compiler logs to move into <c>fragments/</c>.</param>
    /// <param name="graph">The merged extracted graph.</param>
    /// <param name="commit">The source commit, when known.</param>
    /// <param name="capturedUtc">The ISO-8601 UTC capture time, when recorded.</param>
    /// <returns>The manifest written into the bundle.</returns>
    public static CaptureManifest WriteMerged(
        string bundleDirectory, IReadOnlyList<string> fragmentLogPaths, CaptureResult graph, string? commit, string? capturedUtc)
    {
        Directory.CreateDirectory(bundleDirectory);
        var fragmentsDir = FragmentsDir(bundleDirectory);
        Directory.CreateDirectory(fragmentsDir);

        var index = 0;
        foreach (var fragment in fragmentLogPaths)
        {
            // Name each fragment stably by index so the layout is deterministic; the content identifies the project.
            var dest = Path.Combine(fragmentsDir, $"fragment-{index:D4}.complog");
            index++;
            if (Path.GetFullPath(fragment) == Path.GetFullPath(dest))
                continue;
            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(fragment, dest);
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
