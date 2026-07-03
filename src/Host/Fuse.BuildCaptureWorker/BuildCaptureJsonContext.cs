using System.Text.Json.Serialization;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     Source-generated JSON context for the worker's stdout contract, per the project invariant that JSON uses
///     a source-generated <see cref="JsonSerializerContext" /> rather than reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CaptureResult))]
[JsonSerializable(typeof(CapturedProject))]
public sealed partial class BuildCaptureJsonContext : JsonSerializerContext;
