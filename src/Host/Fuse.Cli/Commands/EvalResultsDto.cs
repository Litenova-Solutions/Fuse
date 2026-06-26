using System.Text.Json.Serialization;

namespace Fuse.Cli.Commands;

/// <summary>
///     The JSON shape of a semantics evaluation run.
/// </summary>
/// <param name="Suite">The suite name.</param>
/// <param name="Fixtures">The number of fixtures scored.</param>
/// <param name="EdgesExpected">The total ground-truth edge count.</param>
/// <param name="EdgesMatched">The total matched edge count.</param>
/// <param name="FalsePositives">The total false positives over scored edge types.</param>
/// <param name="Recall">Matched over expected.</param>
/// <param name="Precision">Matched over matched plus false positives.</param>
/// <param name="Results">Per-fixture results.</param>
public sealed record EvalResultsDto(
    string Suite,
    int Fixtures,
    int EdgesExpected,
    int EdgesMatched,
    int FalsePositives,
    double Recall,
    double Precision,
    IReadOnlyList<FixtureResultDto> Results);

/// <summary>
///     The JSON shape of one fixture's evaluation result.
/// </summary>
/// <param name="Name">The fixture name.</param>
/// <param name="Expected">The ground-truth edge count.</param>
/// <param name="Matched">The matched edge count.</param>
/// <param name="FalsePositives">The false positives over scored edge types.</param>
/// <param name="Missed">The missed edges, formatted.</param>
public sealed record FixtureResultDto(
    string Name,
    int Expected,
    int Matched,
    int FalsePositives,
    IReadOnlyList<string> Missed);

/// <summary>
///     Source-generated serializer context for evaluation results JSON.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(EvalResultsDto))]
internal partial class FuseEvalJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
