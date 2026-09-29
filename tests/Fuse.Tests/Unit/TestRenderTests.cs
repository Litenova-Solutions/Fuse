using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>What <c>fuse test</c> prints for a run that wrote no TRX results, which is every Microsoft.Testing.Platform run.</summary>
public class TestRenderTests
{
    [Fact]
    public void A_run_that_ends_without_results_names_the_run_and_its_exit_code()
    {
        // Microsoft.Testing.Platform's own report of a failed test; no line of it is a build error.
        var output = "Running tests from Lib.Tests.dll\nfailed Multiplies (12ms)\n  Assert.Equal() Failure: Values differ\nTest run summary: Failed!\n  failed: 1\n";
        var result = TestOperation.WithoutResults(new ProcessResult(2, output), FixtureRepo.CheckoutRoot, "the test run of Lib.Tests", "ran every test in 1 test project(s)", 1.5);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal("Running tests from Lib.Tests.dll", lines[0]);
        Assert.Equal("  failed: 1", lines[^2]);
        Assert.StartsWith("fuse: the test run of Lib.Tests exited with code 2 and produced no results in ", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("build", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_whose_output_holds_build_errors_is_a_failed_test_build()
    {
        var output = "C:/nowhere/App.Tests/ReportTests.cs(6,70): error CS1026: ) expected [C:/nowhere/App.Tests/App.Tests.csproj]\n";
        var result = TestOperation.WithoutResults(new ProcessResult(1, output), FixtureRepo.CheckoutRoot, "the test run of App.Tests", "ran every test in 1 test project(s)", 1.5);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal("C:/nowhere/App.Tests/ReportTests.cs(6,70): error CS1026: ) expected", lines[0]);
        Assert.StartsWith("fuse: test build failed with 1 error(s) in ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_testing_platform_run_that_passed_reports_the_summary()
    {
        var result = TestOperation.WithoutResults(null, FixtureRepo.CheckoutRoot, "the test run of Lib.Tests", "ran every test in 1 test project(s)", 1.5);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.StartsWith("fuse: tests passed in ", result.Text, StringComparison.Ordinal);
        Assert.EndsWith("; ran every test in 1 test project(s)", result.Text, StringComparison.Ordinal);
    }
}
