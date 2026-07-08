using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: the TRX verdict parser reads per-test outcomes from the micro-host's VSTest output. Passed and failed map
// straight through; anything else (NotExecuted, Timeout, and so on) is not-run, so a test the host could not run
// is never reported as passed. Malformed or empty input yields no verdicts rather than throwing.
public sealed class TrxResultParserTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Ns.A.Passes" outcome="Passed" />
            <UnitTestResult testName="Ns.A.Fails" outcome="Failed" />
            <UnitTestResult testName="Ns.A.Skipped" outcome="NotExecuted" />
          </Results>
        </TestRun>
        """;

    [Fact]
    public void Parses_passed_failed_and_not_run_outcomes()
    {
        var verdicts = TrxResultParser.Parse(Sample);

        Assert.Equal(3, verdicts.Count);
        Assert.Equal("passed", Assert.Single(verdicts, v => v.Name == "Ns.A.Passes").Outcome);
        Assert.Equal("failed", Assert.Single(verdicts, v => v.Name == "Ns.A.Fails").Outcome);
        Assert.Equal("not-run", Assert.Single(verdicts, v => v.Name == "Ns.A.Skipped").Outcome);
    }

    [Fact]
    public void Empty_or_malformed_input_yields_no_verdicts()
    {
        Assert.Empty(TrxResultParser.Parse(""));
        Assert.Empty(TrxResultParser.Parse("not xml <<<"));
        Assert.Empty(TrxResultParser.Parse("<TestRun />"));
    }

    [Fact]
    public void An_unknown_outcome_is_treated_as_not_run_never_passed()
    {
        var trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Ns.A.Weird" outcome="Timeout" /></Results>
            </TestRun>
            """;
        Assert.Equal("not-run", Assert.Single(TrxResultParser.Parse(trx)).Outcome);
    }
}
