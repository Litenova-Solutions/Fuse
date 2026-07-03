using System.Text.Json.Serialization;

namespace Fuse.Indexing;

/// <summary>
///     One rehydrated C# project from an N4 tier-1 build capture, carrying the extracted graph the parent writes
///     to the store. Defined in this shared assembly (not the worker) so the parent process can deserialize the
///     worker's output without referencing the worker's Basic.CompilerLog closure, which conflicts with the
///     parent's MSBuildWorkspace.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="FilePath">The project file path.</param>
/// <param name="AssemblyName">The output assembly name.</param>
/// <param name="ErrorCount">The number of compile errors in the rehydrated compilation.</param>
/// <param name="TypeCount">The number of named types the compilation declares.</param>
/// <param name="SymbolCount">The number of symbols the semantic extractor produced.</param>
/// <param name="NodeCount">The number of semantic-graph nodes the wiring analyzers produced.</param>
/// <param name="EdgeCount">The number of semantic-graph edges the wiring analyzers produced.</param>
/// <param name="Symbols">The extracted symbol records.</param>
/// <param name="Nodes">The semantic-graph node records.</param>
/// <param name="Edges">The semantic-graph edge records.</param>
/// <param name="Routes">The route records.</param>
/// <param name="DiRegistrations">The DI registration records.</param>
/// <param name="OptionsBindings">The options binding records.</param>
public sealed record CapturedProject(
    string Name,
    string FilePath,
    string? AssemblyName,
    int ErrorCount,
    int TypeCount,
    int SymbolCount = 0,
    int NodeCount = 0,
    int EdgeCount = 0,
    IReadOnlyList<SymbolRecord>? Symbols = null,
    IReadOnlyList<NodeRecord>? Nodes = null,
    IReadOnlyList<SemanticEdgeRecord>? Edges = null,
    IReadOnlyList<RouteRecord>? Routes = null,
    IReadOnlyList<DiRegistrationRecord>? DiRegistrations = null,
    IReadOnlyList<OptionsBindingRecord>? OptionsBindings = null);

/// <summary>
///     The outcome of a build capture: success plus the rehydrated projects, or a concrete failure reason.
/// </summary>
/// <param name="Succeeded">Whether the build succeeded and at least one C# compilation was rehydrated.</param>
/// <param name="Reason">The concrete failure reason when <see cref="Succeeded" /> is false; otherwise null.</param>
/// <param name="Projects">The rehydrated C# projects and their extracted graph; empty on failure.</param>
public sealed record CaptureResult(bool Succeeded, string? Reason, IReadOnlyList<CapturedProject> Projects)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="projects">The rehydrated projects.</param>
    /// <returns>A succeeded result.</returns>
    public static CaptureResult Ok(IReadOnlyList<CapturedProject> projects) => new(true, null, projects);

    /// <summary>Creates a failed result.</summary>
    /// <param name="reason">The concrete failure reason.</param>
    /// <returns>A failed result with no projects.</returns>
    public static CaptureResult Failed(string reason) => new(false, reason, []);
}

/// <summary>
///     Source-generated JSON context for the build-capture worker-to-parent contract, per the project invariant
///     that JSON uses a source-generated <see cref="JsonSerializerContext" /> rather than reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CaptureResult))]
[JsonSerializable(typeof(CapturedProject))]
[JsonSerializable(typeof(SymbolRecord))]
[JsonSerializable(typeof(NodeRecord))]
[JsonSerializable(typeof(SemanticEdgeRecord))]
[JsonSerializable(typeof(RouteRecord))]
[JsonSerializable(typeof(DiRegistrationRecord))]
[JsonSerializable(typeof(OptionsBindingRecord))]
public sealed partial class BuildCaptureJsonContext : JsonSerializerContext;
