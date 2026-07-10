using System.Text.Json.Serialization;

namespace Fuse.Workspace;

/// <summary>
///     The request Fuse sends the out-of-process test micro-host (T1): the emitted test assembly to load, the
///     reference assemblies it needs, and the fully qualified names of the covering subset to run. The host loads
///     the assembly in an isolated load context and runs exactly these tests, so only the covering set executes.
/// </summary>
/// <param name="AssemblyPath">The path of the emitted test assembly to load and run.</param>
/// <param name="ReferencePaths">The reference assembly paths the emitted assembly needs to load.</param>
/// <param name="TestFullyQualifiedNames">The fully qualified names of the tests to run (the covering subset).</param>
public sealed record TestHostRequest(
    string AssemblyPath,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<string> TestFullyQualifiedNames);

/// <summary>
///     One test's result from the micro-host (T1).
/// </summary>
/// <param name="Name">The fully qualified test name.</param>
/// <param name="Outcome">The outcome: <c>passed</c>, <c>failed</c>, or <c>not-run</c>.</param>
/// <param name="Message">The failure message when the outcome is failed; otherwise null.</param>
public sealed record TestCaseResult(string Name, string Outcome, string? Message);

/// <summary>
///     The response the out-of-process test micro-host returns (T1): the per-test results, the requested tests it
///     could not run (with a reason), and a host-level error when the run could not start at all. A test that could
///     not be executed is reported not-runnable by name, never silently counted as passed.
/// </summary>
/// <param name="Results">The per-test results for the tests that ran.</param>
/// <param name="NotRunnable">The requested tests the host could not run, each as "name: reason".</param>
/// <param name="Error">A host-level error when the run could not start (assembly load failure, no test framework); otherwise null.</param>
public sealed record TestHostResponse(
    IReadOnlyList<TestCaseResult> Results,
    IReadOnlyList<string> NotRunnable,
    string? Error);

/// <summary>
///     Source-generated JSON context for the test micro-host wire contract (T1), so the request and response
///     serialize without reflection, per the project's reflection-free serialization invariant. Fuse and the host
///     executable share these shapes over stdin/stdout.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TestHostRequest))]
[JsonSerializable(typeof(TestHostResponse))]
[JsonSerializable(typeof(TestCaseResult))]
public sealed partial class TestHostJsonContext : JsonSerializerContext;
