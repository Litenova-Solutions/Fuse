namespace Fuse.Protocol;

/// <summary>One test of a shadow run as the client reports it back to the engine, with the plan it ran from.</summary>
/// <param name="Test">The fully qualified test name.</param>
/// <param name="Verdict">How the test ended.</param>
/// <param name="Project">Absolute path of the test project file the test ran in, so two projects holding one test name stay apart.</param>
internal sealed record ReportedTestResult(string Test, ReportedVerdict Verdict, string Project)
{
    /// <summary>The test. A line without it reads as empty, which matches no class.</summary>
    public string Test { get; init; } = Test ?? "";

    /// <summary>The project. A line without it reads as empty, which matches no plan.</summary>
    public string Project { get; init; } = Project ?? "";
}
