using Fuse.Dotnet;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class OutputRecordTests
{
    [Fact]
    public void A_platform_run_records_each_failure_with_its_printed_duration()
    {
        var output = """
            failed Ns.T.Slow (1s 200ms)
              boom
            failed Ns.T.Quick (12ms)
              bang
            Test run summary: Failed!
              failed: 2
              succeeded: 3
              skipped: 1
            """;

        var outcome = TestingPlatformOutput.Read(output, FixtureRepo.CheckoutRoot.Path)!;

        Assert.Equal((3, 2, 1), (outcome.Passed, outcome.Failed, outcome.Skipped));
        Assert.Equal(
            [("Ns.T.Slow", 1200.0), ("Ns.T.Quick", 12.0)],
            outcome.Records.Select(r => (r.Name, r.Duration.TotalMilliseconds)));
        Assert.All(outcome.Records, r => Assert.Equal(TestVerdict.Failed, r.Verdict));
    }

    [Fact]
    public void An_xunit_run_records_its_failures_without_durations()
    {
        var output = """
                Ns.T.Fails [FAIL]
                  boom
            === TEST EXECUTION SUMMARY ===
              Ns.T  Total: 2, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0
            """;

        var outcome = XUnitOutput.Read(output, FixtureRepo.CheckoutRoot.Path)!;

        Assert.Equal((1, 1, 0), (outcome.Passed, outcome.Failed, outcome.Skipped));
        var record = Assert.Single(outcome.Records);
        Assert.Equal(("Ns.T.Fails", TestVerdict.Failed, TimeSpan.Zero), (record.Name, record.Verdict, record.Duration));
    }
}
