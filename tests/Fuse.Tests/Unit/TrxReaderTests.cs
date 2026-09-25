using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class TrxReaderTests
{
    [Fact]
    public void Reads_counts_failures_and_repository_frames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "trx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var root = directory;
            File.WriteAllText(Path.Combine(directory, "a.trx"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="Ns.T.Passes" outcome="Passed" />
                    <UnitTestResult testName="Ns.T.Skips" outcome="NotExecuted" />
                    <UnitTestResult testName="Ns.T.Fails" outcome="Failed">
                      <Output>
                        <ErrorInfo>
                          <Message>Assert.Equal() Failure
                Expected: 1
                Actual:   2</Message>
                          <StackTrace>   at Xunit.Assert.Equal() in /_/src/Assert.cs:line 1
                   at Ns.T.Fails() in {root}{Path.DirectorySeparatorChar}T.cs:line 9</StackTrace>
                        </ErrorInfo>
                      </Output>
                    </UnitTestResult>
                  </Results>
                </TestRun>
                """);
            var outcome = TrxReader.ReadDirectory(directory, root)!;
            Assert.Equal(1, outcome.Passed);
            Assert.Equal(1, outcome.Failed);
            Assert.Equal(1, outcome.Skipped);
            var failure = Assert.Single(outcome.Failures);
            Assert.Equal("Ns.T.Fails", failure.Name);
            Assert.StartsWith("Assert.Equal() Failure", failure.Message, StringComparison.Ordinal);
            Assert.Equal(["at Ns.T.Fails() in T.cs:line 9"], failure.Frames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_directory_reads_as_null()
    {
        Assert.Null(TrxReader.ReadDirectory(Path.Combine(Path.GetTempPath(), "fuse-no-such-dir"), "C:\\"));
    }
}
