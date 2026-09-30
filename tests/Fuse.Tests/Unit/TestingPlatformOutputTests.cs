using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

/// <summary>
///     A Microsoft.Testing.Platform run's counts and failures, read from what it prints. The output below is an xunit.v3
///     run's, as <c>dotnet test --project</c> printed it with its paths shortened.
/// </summary>
public class TestingPlatformOutputTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    private static readonly string FailingRun = string.Join('\n',
        $@"Running tests from {Root}\bin\Debug\net10.0\T.dll (net10.0|x64)",
        "skipped T.Tests.Skipped (0ms)",
        "  later",
        $@"  from {Root}\bin\Debug\net10.0\T.dll (net10.0|x64)",
        "failed T.Tests.Fails (4ms)",
        $@"  from {Root}\bin\Debug\net10.0\T.dll (net10.0|x64)",
        "  Assert.Equal() Failure: Values differ",
        "  Expected: 1",
        "  Actual:   2",
        $"    at T.Tests.Fails() in {Path.Combine(Root, "Tests.cs")}:6",
        "    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)",
        "failed T.Tests.Rows(x: 2) (1s 056ms)",
        "  Assert.True() Failure",
        $@"{Root}\bin\Debug\net10.0\T.dll (net10.0|x64) failed with 2 error(s) (1s 056ms)",
        "Exit code: 2",
        "",
        "Test run summary: Failed!",
        "  total: 6",
        "  failed: 2",
        "  succeeded: 3",
        "  skipped: 1",
        "  duration: 4s 564ms",
        "Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)");

    [Fact]
    public void The_counts_come_from_the_run_summary()
    {
        var results = TestingPlatformOutput.Read(FailingRun, Root);

        Assert.NotNull(results);
        Assert.Equal((3, 2, 1), (results.Passed, results.Failed, results.Skipped));
    }

    [Fact]
    public void Each_failed_test_has_its_message_and_the_frames_inside_the_repository()
    {
        var failures = TestingPlatformOutput.Read(FailingRun, Root)!.Failures;

        Assert.Equal(["T.Tests.Fails", "T.Tests.Rows(x: 2)"], failures.Select(f => f.Name));
        Assert.Equal("Assert.Equal() Failure: Values differ\nExpected: 1\nActual:   2", failures[0].Message);
        Assert.Equal(["at T.Tests.Fails() in Tests.cs:6"], failures[0].Frames);
        Assert.Equal("Assert.True() Failure", failures[1].Message);
        Assert.Empty(failures[1].Frames);
    }

    [Fact]
    public void A_passing_run_has_its_counts_and_no_failures()
    {
        const string output = "Test run summary: Passed!\n  total: 505\n  failed: 0\n  succeeded: 505\n  skipped: 0\n  duration: 10m 41s 999ms\n";

        var results = TestingPlatformOutput.Read(output, Root);

        Assert.NotNull(results);
        Assert.Equal((505, 0, 0), (results.Passed, results.Failed, results.Skipped));
        Assert.Empty(results.Failures);
    }

    [Fact]
    public void A_run_whose_summary_counts_an_error_has_no_results()
    {
        const string output = "Fuse.Tests.dll (net10.0) Zero tests ran\nExit code: 5\nTest run summary: Zero tests ran\n  error: 1\n  total: 0\n  failed: 0\n  succeeded: 0\n  skipped: 0\n  duration: 761ms\n";

        Assert.Null(TestingPlatformOutput.Read(output, Root));
    }

    [Fact]
    public void Output_without_a_run_summary_has_no_results() =>
        Assert.Null(TestingPlatformOutput.Read("Tests.cs(4,6): error CS0246: The type or namespace name 'Fact' could not be found", Root));
}
