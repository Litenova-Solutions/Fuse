using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Benchmarks;

/// <summary>
///     Source-generated serializer context for benchmark datasets and results. Honors the repo-wide
///     reflection-free serialization invariant: no runtime reflection-based JSON is used.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CorpusManifest))]
[JsonSerializable(typeof(PrRecord[]))]
[JsonSerializable(typeof(EvalDataset))]
[JsonSerializable(typeof(SuiteResult))]
public partial class BenchmarkJsonContext : JsonSerializerContext;
