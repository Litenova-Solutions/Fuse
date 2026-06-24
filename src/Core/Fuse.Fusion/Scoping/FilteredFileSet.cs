namespace Fuse.Fusion;

// The result of a scoping mode (focus, query, or changes): the selected files plus the provenance, scores,
// optional review preamble, optional symbol-slice request, and optional per-file selected members that the
// downstream reduction, packing, and emission stages consume. Internal so the scoping pipelines can return it.
internal sealed record FilteredFileSet(
    IReadOnlyList<Fuse.Collection.Models.SourceFile> Files,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance,
    IReadOnlyDictionary<string, double>? Scores,
    string? Preamble = null,
    SymbolSliceRequest? Slice = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? SelectedMembers = null);

// A request to slice specific files down to one member and its dependencies (the focus seed slice).
internal sealed record SymbolSliceRequest(IReadOnlySet<string> Paths, string Member);
