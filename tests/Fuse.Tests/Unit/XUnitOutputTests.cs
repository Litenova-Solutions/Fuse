using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

/// <summary>
///     What the xUnit v3 application prints when it runs on its own: counts from its execution summary, failures with
///     their frames in the repository, and no answer for a run with errors or without a summary (#57).
/// </summary>
public class XUnitOutputTests
{
    private const string Root = "C:\\repo";

    [Fact]
    public void A_passing_run_reads_its_counts()
    {
        var output = XUnitOutput.Read("""
            xUnit.net v3 In-Process Runner v3.2.2 (64-bit .NET 10.0.12)
              Discovering: Tests
              Discovered:  Tests
              Starting:    Tests
              Finished:    Tests (ID = 'abc')
            === TEST EXECUTION SUMMARY ===
               Tests  Total: 2, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.391s
            """, Root);

        Assert.NotNull(output);
        Assert.Equal((2, 0, 0), (output.Passed, output.Failed, output.Skipped));
        Assert.Empty(output.Failures);
    }

    [Fact]
    public void A_failing_run_reads_the_failure_its_message_and_its_frame()
    {
        var output = XUnitOutput.Read("""
              Starting:    Tests
                Platform.Tests.CalcTests.Adds [FAIL]
                  Assert.Equal() Failure: Values differ
                  Expected: 4
                  Actual:   3
                  Stack Trace:
                    C:\repo\Tests\CalcTests.cs(8,0): at Platform.Tests.CalcTests.Adds()
                       at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
              Finished:    Tests (ID = 'abc')
            === TEST EXECUTION SUMMARY ===
               Tests  Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 0.120s
            """, Root);

        Assert.NotNull(output);
        Assert.Equal((0, 1, 0), (output.Passed, output.Failed, output.Skipped));
        var failure = Assert.Single(output.Failures);
        Assert.Equal("Platform.Tests.CalcTests.Adds", failure.Name);
        Assert.Contains("Assert.Equal() Failure: Values differ", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["at Platform.Tests.CalcTests.Adds() in Tests/CalcTests.cs:line 8"], failure.Frames);
    }

    [Fact]
    public void A_run_that_matched_no_test_reads_zero_counts()
    {
        var output = XUnitOutput.Read("""
            === TEST EXECUTION SUMMARY ===
               Tests  Total: 0
            """, Root);

        Assert.NotNull(output);
        Assert.Equal(0, output.Total);
    }

    [Fact]
    public void Skipped_and_not_run_both_count_as_skipped()
    {
        var output = XUnitOutput.Read("""
            === TEST EXECUTION SUMMARY ===
               Tests  Total: 4, Errors: 0, Failed: 0, Skipped: 1, Not Run: 1, Time: 0.391s
            """, Root);

        Assert.NotNull(output);
        Assert.Equal((2, 0, 2), (output.Passed, output.Failed, output.Skipped));
    }

    [Fact]
    public void A_run_with_errors_or_without_a_summary_reads_as_no_result()
    {
        Assert.Null(XUnitOutput.Read("""
            === TEST EXECUTION SUMMARY ===
               Tests  Total: 1, Errors: 1, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.391s
            """, Root));
        Assert.Null(XUnitOutput.Read("error: unknown option: --nope", Root));
    }
}
