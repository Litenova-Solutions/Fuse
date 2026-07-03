using System.Text.Json.Serialization;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Source-generated JSON context for the worker's stdout contract, per the project invariant that JSON uses
///     a source-generated <see cref="JsonSerializerContext" /> rather than reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CaptureResult))]
[JsonSerializable(typeof(CapturedProject))]
[JsonSerializable(typeof(Fuse.Indexing.SymbolRecord))]
[JsonSerializable(typeof(Fuse.Indexing.NodeRecord))]
[JsonSerializable(typeof(Fuse.Indexing.SemanticEdgeRecord))]
[JsonSerializable(typeof(Fuse.Indexing.RouteRecord))]
[JsonSerializable(typeof(Fuse.Indexing.DiRegistrationRecord))]
[JsonSerializable(typeof(Fuse.Indexing.OptionsBindingRecord))]
public sealed partial class BuildCaptureJsonContext : JsonSerializerContext;
