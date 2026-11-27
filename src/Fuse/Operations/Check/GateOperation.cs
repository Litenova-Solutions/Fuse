using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Operations.Session;
using Fuse.Operations.Test;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
namespace Fuse.Operations.Check;

/// <summary>
///     <c>fuse gate</c>: single deterministic worker verification command. Runs <c>fuse check</c> (with optional
///     <c>--brief</c> repair packets), and when clean and <c>--test</c> is set, runs affected tests with fail-fast
///     <c>--max-failures</c>. Exits 0 when green, 1 when errors or test failures remain, 2 when refused.
/// </summary>
internal static partial class GateOperation
{
    internal const string Usage = "usage: fuse gate [--session <name>] [--brief] [--test] [--max-failures <n>] [--json]";

    internal sealed record GateArguments(string? Session, bool Brief, bool RunTests, int MaxFailures, bool Json);

    public static (GateArguments? Arguments, string? Refusal) Parse(IReadOnlyList<string> args)
    {
        string? session = null;
        var brief = false;
        var runTests = false;
        var maxFailures = TestOperation.DefaultMaxFailures;
        var json = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (token is "--help" or "-h")
                continue;
            if (token == "--brief")
            {
                brief = true;
                continue;
            }

            if (token == "--test")
            {
                runTests = true;
                continue;
            }

            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token == "--session" || token.StartsWith("--session=", StringComparison.Ordinal))
            {
                var value = token.StartsWith("--session=", StringComparison.Ordinal)
                    ? token["--session=".Length..]
                    : ++i < args.Count ? args[i] : null;
                if (string.IsNullOrWhiteSpace(value))
                    return (null, "fuse: --session takes the session's id");
                session = value;
                continue;
            }

            if (token == "--max-failures" || token.StartsWith("--max-failures=", StringComparison.Ordinal))
            {
                var text = token.StartsWith("--max-failures=", StringComparison.Ordinal)
                    ? token["--max-failures=".Length..]
                    : ++i < args.Count ? args[i] : null;
                if (text is null || !int.TryParse(text, CultureInfo.InvariantCulture, out maxFailures) || maxFailures < 1 || maxFailures > CheckArguments.MaxErrorsLimit)
                    return (null, $"fuse: --max-failures takes a count of 1 to {CheckArguments.MaxErrorsLimit}, not '{text ?? ""}'");
                continue;
            }

            return (null, $"fuse: '{token}' is not a fuse gate option\n{Usage}");
        }

        return (new GateArguments(session, brief, runTests, maxFailures, json), null);
    }

    public static async Task<OperationResult> RunAsync(
        RepoRoot root,
        string workingDirectory,
        IReadOnlyList<string> args,
        TextWriter? live,
        CancellationToken cancellationToken)
    {
        var (parsed, refusal) = Parse(args);
        if (parsed is null)
            return new OperationResult(Outcome.Unanswered, refusal!);

        var session = parsed.Session ?? SessionOperation.EnvironmentSession;
        var (checkResult, checkResponse) = await CheckOperation.RunAsync(
            root,
            files: null,
            session: session,
            waitForLoad: true,
            compilationWideAnalyzers: true,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken,
            maxErrors: parsed.Json ? int.MaxValue : 0,
            json: false,
            includeTreeStamp: true,
            includeNonCSharp: true,
            brief: parsed.Brief).ConfigureAwait(false);

        var checkReport = (checkResponse as EngineResponse.CheckAnswered)?.Report;
        if (checkResult.Outcome != Outcome.Clean)
        {
            if (parsed.Json)
            {
                var payload = new GateJsonReport(
                    Passed: false,
                    ExitCode: checkResult.ExitCode,
                    Stage: "check",
                    ErrorCount: checkReport?.ErrorCount ?? 0,
                    Errors: checkReport?.Errors ?? [],
                    CheckOutput: checkResult.Text,
                    TestOutput: null);
                return new OperationResult(checkResult.Outcome, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
            }

            return checkResult;
        }

        if (!parsed.RunTests)
        {
            if (parsed.Json)
            {
                var payload = new GateJsonReport(
                    Passed: true,
                    ExitCode: 0,
                    Stage: "check",
                    ErrorCount: 0,
                    Errors: [],
                    CheckOutput: checkResult.Text,
                    TestOutput: null);
                return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
            }

            return checkResult;
        }

        var testResult = await TestOperation.RunAsync(
            root,
            workingDirectory,
            arguments: [],
            all: false,
            cancellationToken: cancellationToken,
            session: session,
            live: parsed.Json ? null : live,
            failed: false,
            maxFailures: parsed.MaxFailures).ConfigureAwait(false);

        if (parsed.Json)
        {
            var payload = new GateJsonReport(
                Passed: testResult.Outcome == Outcome.Clean,
                ExitCode: testResult.ExitCode,
                Stage: "test",
                ErrorCount: 0,
                Errors: [],
                CheckOutput: checkResult.Text,
                TestOutput: testResult.Text);
            return new OperationResult(testResult.Outcome, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
        }

        var combined = new StringBuilder();
        combined.Append(checkResult.Text).Append('\n').Append(testResult.Text);
        return new OperationResult(testResult.Outcome, combined.ToString());
    }

    private sealed record GateJsonReport(
        bool Passed,
        int ExitCode,
        string Stage,
        int ErrorCount,
        ReportedError[] Errors,
        string CheckOutput,
        string? TestOutput);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(GateJsonReport))]
    private sealed partial class GateJson : JsonSerializerContext;
}
