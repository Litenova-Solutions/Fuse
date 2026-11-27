using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Identity;
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

    public static ParseResult<GateArguments> Parse(IReadOnlyList<string> args)
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
                    return new ParseResult<GateArguments>.Refused("fuse: --session takes the session's id");
                session = value;
                continue;
            }

            if (token == "--max-failures" || token.StartsWith("--max-failures=", StringComparison.Ordinal))
            {
                var text = token.StartsWith("--max-failures=", StringComparison.Ordinal)
                    ? token["--max-failures=".Length..]
                    : ++i < args.Count ? args[i] : null;
                if (text is null || !int.TryParse(text, CultureInfo.InvariantCulture, out maxFailures) || maxFailures < 1 || maxFailures > CheckArguments.MaxErrorsLimit)
                    return new ParseResult<GateArguments>.Refused($"fuse: --max-failures takes a count of 1 to {CheckArguments.MaxErrorsLimit}, not '{text ?? ""}'");
                continue;
            }

            return new ParseResult<GateArguments>.Refused($"fuse: '{token}' is not a fuse gate option\n{Usage}");
        }

        return new ParseResult<GateArguments>.Parsed(new GateArguments(session, brief, runTests, maxFailures, json));
    }

    public static async Task<OperationResult> RunAsync(
        RepoRoot root,
        string workingDirectory,
        IReadOnlyList<string> args,
        TextWriter? live,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var parsed = Parse(args);
        if (parsed is ParseResult<GateArguments>.Refused refused)
            return new OperationResult(Outcome.Unanswered, refused.Message);
        var arguments = ((ParseResult<GateArguments>.Parsed)parsed).Value;

        var session = arguments.Session ?? SessionOperation.EnvironmentSession;
        var (checkResult, checkResponse) = await CheckOperation.RunAsync(
            root,
            new CheckInvocation(
                Files: null,
                Session: SessionId.TryParse(session, out var checkSession) ? checkSession : null,
                WaitForLoad: true,
                CompilationWideAnalyzers: true,
                Limit: arguments.Json ? ErrorLimit.All : ErrorLimit.Default,
                Json: false,
                IncludeTreeStamp: true,
                IncludeNonCSharp: true,
                Brief: arguments.Brief,
                InteractiveFast: false,
                OnBusy: BusyNotice.For()),
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);

        var checkReport = (checkResponse as EngineResponse.CheckAnswered)?.Report;
        if (checkResult.Outcome != Outcome.Clean)
        {
            if (arguments.Json)
            {
                var payload = new GateJsonReport(
                    Passed: false,
                    ExitCode: checkResult.ExitCode,
                    Stage: "check",
                    ErrorCount: checkReport?.ErrorCount ?? 0,
                    Errors: checkReport?.Errors ?? [],
                    CheckOutput: checkResult.Text,
                    TestOutput: null,
                    ElapsedMs: watch.ElapsedMilliseconds);
                return new OperationResult(checkResult.Outcome, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
            }

            return checkResult;
        }

        if (!arguments.RunTests)
        {
            if (arguments.Json)
            {
                var payload = new GateJsonReport(
                    Passed: true,
                    ExitCode: 0,
                    Stage: "check",
                    ErrorCount: 0,
                    Errors: [],
                    CheckOutput: checkResult.Text,
                    TestOutput: null,
                    ElapsedMs: watch.ElapsedMilliseconds);
                return new OperationResult(Outcome.Clean, JsonSerializer.Serialize(payload, GateJson.Default.GateJsonReport));
            }

            return checkResult;
        }

        var testResult = await TestOperation.RunAsync(new TestInvocation(root, workingDirectory, [], false, session, arguments.Json ? null : live, false, false, false, ErrorLimit.FromCount(arguments.MaxFailures)), cancellationToken).ConfigureAwait(false);

        if (arguments.Json)
        {
            var payload = new GateJsonReport(
                Passed: testResult.Outcome == Outcome.Clean,
                ExitCode: testResult.ExitCode,
                Stage: "test",
                ErrorCount: 0,
                Errors: [],
                CheckOutput: checkResult.Text,
                TestOutput: testResult.Text,
                ElapsedMs: watch.ElapsedMilliseconds);
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
        string? TestOutput,
        long ElapsedMs);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(GateJsonReport))]
    private sealed partial class GateJson : JsonSerializerContext;
}
