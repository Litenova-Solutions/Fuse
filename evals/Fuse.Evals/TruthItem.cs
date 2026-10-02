namespace Fuse.Evals;

/// <summary>
///     The truth side's answer for HEAD or for one case: for the correctness suite the error lines a real <c>dotnet build</c>
///     reported, for the selection suite the tests a full <c>dotnet test</c> run saw failing.
/// </summary>
/// <param name="Names">The error lines, or the names of the failing tests.</param>
/// <param name="Total">How many tests ran, for the selection suite; 0 for the correctness suite.</param>
/// <param name="Seconds">How long the dotnet command took.</param>
/// <param name="BuildFailed">True when a test project did not build, so the run has no test results for it.</param>
/// <param name="ResultFiles">How many TRX files the test run wrote, so a case whose run lost a test project shows.</param>
/// <param name="Origin">Where the answer was measured: a GitHub Actions run and job, or this machine.</param>
internal sealed record TruthItem(List<string> Names, int Total, double Seconds, bool BuildFailed, int ResultFiles, string Origin);
