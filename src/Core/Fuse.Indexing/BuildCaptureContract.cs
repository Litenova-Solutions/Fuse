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
/// <param name="TargetFramework">The target framework represented by this compiler invocation, when known.</param>
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
    IReadOnlyList<OptionsBindingRecord>? OptionsBindings = null,
    string? TargetFramework = null);

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
///     One compiler diagnostic from a speculative typecheck (R1 <c>fuse_check</c>): the shape an agent needs to
///     fix a build error without running <c>dotnet build</c>.
/// </summary>
/// <param name="Id">The diagnostic id (for example <c>CS1061</c>).</param>
/// <param name="Severity">The severity (<c>Error</c>, <c>Warning</c>, <c>Info</c>, <c>Hidden</c>).</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="FilePath">The file the diagnostic is in, when known.</param>
/// <param name="Line">The 1-based line, when known.</param>
public sealed record CheckDiagnostic(string Id, string Severity, string Message, string? FilePath, int Line);

/// <summary>
///     The outcome of a speculative typecheck of a proposed patch against a build-captured compilation (R1). The
///     patch is applied to the rehydrated compilation in memory (no disk write, no <c>dotnet build</c>); the
///     diagnostics are those the real compiler would report, because the compilation shares the real build's
///     inputs (tier-1). When capture is unavailable the result abstains rather than guessing.
/// </summary>
/// <param name="Verified">Whether the typecheck ran (a compilation was available and the patch applied).</param>
/// <param name="Reason">Why the check could not verify (abstention), when <see cref="Verified" /> is false.</param>
/// <param name="Diagnostics">The compiler diagnostics for the changed document, when verified.</param>
public sealed record CheckResult(bool Verified, string? Reason, IReadOnlyList<CheckDiagnostic> Diagnostics)
{
    /// <summary>Whether the changed document has no error-severity diagnostics (a clean speculative build).</summary>
    public bool IsClean => Verified && !Diagnostics.Any(d => d.Severity == "Error");

    /// <summary>
    ///     The verification grade (T0, Decision D11): <c>oracle</c> when a resident or build-captured compilation
    ///     answered speculatively (sub-second, no build), <c>build</c> when Fuse ran the real toolchain and parsed
    ///     its diagnostics (ground truth, tens of seconds), or <c>abstain</c> when neither was possible and the
    ///     missing prerequisite is named in <see cref="Reason" />. Defaults to <c>oracle</c> so the existing
    ///     speculative path and any pre-T0 worker output deserialize unchanged.
    /// </summary>
    public string Grade { get; init; } = "oracle";

    /// <summary>Creates a verified oracle-grade result (speculative typecheck, no build).</summary>
    /// <param name="diagnostics">The compiler diagnostics.</param>
    /// <returns>A verified oracle-grade result.</returns>
    public static CheckResult Ok(IReadOnlyList<CheckDiagnostic> diagnostics) => new(true, null, diagnostics);

    /// <summary>Creates a verified build-grade result (Fuse ran the real toolchain and parsed its diagnostics).</summary>
    /// <param name="diagnostics">The compiler diagnostics parsed from the build.</param>
    /// <returns>A verified build-grade result.</returns>
    public static CheckResult BuildGraded(IReadOnlyList<CheckDiagnostic> diagnostics) =>
        new(true, null, diagnostics) { Grade = "build" };

    /// <summary>Creates an abstention (could not verify at any grade).</summary>
    /// <param name="reason">The concrete reason the check could not verify.</param>
    /// <returns>An unverified result stamped <c>abstain</c>.</returns>
    public static CheckResult Abstain(string reason) => new(false, reason, []) { Grade = "abstain" };
}

/// <summary>
///     One speculative-check request sent to a pooled build-capture check worker (R48): the repo-relative file
///     being changed and a temp file holding its proposed new content. The content is passed by path (never inline)
///     so it is never a length-bounded value on the request line.
/// </summary>
/// <param name="File">The repo-relative path of the file being changed.</param>
/// <param name="ContentPath">The path to a temp file holding the proposed full new content.</param>
public sealed record CheckRequest(string File, string ContentPath);

/// <summary>
///     Source-generated JSON context for the build-capture worker-to-parent contract, per the project invariant
///     that JSON uses a source-generated <see cref="JsonSerializerContext" /> rather than reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CaptureResult))]
[JsonSerializable(typeof(CapturedProject))]
[JsonSerializable(typeof(CheckResult))]
[JsonSerializable(typeof(CheckRequest))]
[JsonSerializable(typeof(CheckDiagnostic))]
[JsonSerializable(typeof(List<CheckDiagnostic>))]
[JsonSerializable(typeof(SymbolRecord))]
[JsonSerializable(typeof(NodeRecord))]
[JsonSerializable(typeof(SemanticEdgeRecord))]
[JsonSerializable(typeof(RouteRecord))]
[JsonSerializable(typeof(DiRegistrationRecord))]
[JsonSerializable(typeof(OptionsBindingRecord))]
public sealed partial class BuildCaptureJsonContext : JsonSerializerContext;
