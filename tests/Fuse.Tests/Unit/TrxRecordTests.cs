using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

public class TrxRecordTests
{
    [Fact]
    public void Each_result_carries_its_verdict_and_duration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "trx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "a.trx"), """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="Ns.T.Passes" outcome="Passed" duration="00:00:01.500" />
                    <UnitTestResult testName="Ns.T.Skips" outcome="NotExecuted" duration="bogus" />
                    <UnitTestResult testName="Ns.T.Fails" outcome="Failed">
                      <Output>
                        <ErrorInfo>
                          <Message>boom</Message>
                          <StackTrace></StackTrace>
                        </ErrorInfo>
                      </Output>
                    </UnitTestResult>
                  </Results>
                </TestRun>
                """);
            var outcome = TrxReader.ReadDirectory(directory, directory)!;

            Assert.Equal(
                [("Ns.T.Passes", TestVerdict.Passed, 1500.0), ("Ns.T.Skips", TestVerdict.Skipped, 0.0), ("Ns.T.Fails", TestVerdict.Failed, 0.0)],
                outcome.Records.Select(r => (r.Name, r.Verdict, r.Duration.TotalMilliseconds)));
            Assert.Equal("Ns.T.Fails", Assert.Single(outcome.Failures).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Add_concatenates_records()
    {
        var first = new TrxResults(1, 0, 0, []) { Records = [new TestRecord("A", TestVerdict.Passed, TimeSpan.FromMilliseconds(1))] };
        var second = new TrxResults(0, 1, 0, [new TestFailure("B", "boom", [])]) { Records = [new TestRecord("B", TestVerdict.Failed, TimeSpan.Zero)] };

        var sum = first.Add(second);

        Assert.Equal((1, 1, 0), (sum.Passed, sum.Failed, sum.Skipped));
        Assert.Equal(["A", "B"], sum.Records.Select(r => r.Name));
    }
}
