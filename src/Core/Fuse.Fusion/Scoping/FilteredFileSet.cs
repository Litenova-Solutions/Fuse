namespace Fuse.Fusion;

/// <summary>
///     The result of a scoping mode (focus, query, or changes): selected files plus provenance, scores,
///     and optional review preamble, symbol slice, and per-file member selection for downstream stages.
/// </summary>
/// <param name="Files">The scoped source files in emission order.</param>
/// <param name="Provenance">Optional per-file provenance labels, or <c>null</c> when not built.</param>
/// <param name="Scores">Optional per-file relevance scores, or <c>null</c> when not built.</param>
/// <param name="Preamble">Optional review preamble text prepended to output.</param>
/// <param name="Slice">Optional symbol-slice request for focus seed narrowing.</param>
/// <param name="SelectedMembers">Optional per-file selected member names for partial emission.</param>
public sealed record FilteredFileSet(
    IReadOnlyList<Fuse.Collection.Models.SourceFile> Files,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance,
    IReadOnlyDictionary<string, double>? Scores,
    string? Preamble = null,
    SymbolSliceRequest? Slice = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? SelectedMembers = null);

/// <summary>
///     A request to slice specific files down to one member and its dependencies (the focus seed slice).
/// </summary>
/// <param name="Paths">File paths to slice.</param>
/// <param name="Member">The member name to keep in each sliced file.</param>
public sealed record SymbolSliceRequest(IReadOnlySet<string> Paths, string Member);
