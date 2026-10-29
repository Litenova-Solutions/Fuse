using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class LastTestResultsTests
{
    [Fact]
    public void Round_trip_keeps_projects_verdicts_and_durations()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var lib = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        LastTestResults.Write(repo.Root,
        [
            (lib, "Lib.Tests", new TrxResults(1, 1, 1,
                [new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", [])])
            {
                Records =
                [
                    new TestRecord("Lib.Tests.CalcTests.Adds", TestVerdict.Passed, TimeSpan.FromMilliseconds(12)),
                    new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.FromMilliseconds(1500)),
                    new TestRecord("Lib.Tests.CalcTests.Skips", TestVerdict.Skipped, TimeSpan.Zero),
                ],
            }),
            ("", "", new TrxResults(0, 1, 0, [new TestFailure("Flakes", "boom", [])])),
        ]);

        var record = LastTestResults.Read(repo.Root)!;

        Assert.Equal(2, record.Count);
        Assert.Equal((lib, "Lib.Tests"), (record[0].Project, record[0].Name));
        Assert.Equal(
            [("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, 1500.0), ("Lib.Tests.CalcTests.Adds", TestVerdict.Passed, 12.0), ("Lib.Tests.CalcTests.Skips", TestVerdict.Skipped, 0.0)],
            record[0].Tests.Select(t => (t.Name, t.Verdict, t.Duration.TotalMilliseconds)));
        Assert.Equal(("", ""), (record[1].Project, record[1].Name));
        Assert.Equal("Flakes", Assert.Single(record[1].Tests).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"version":2,"projects":[]}""")]
    [InlineData("""{"version":1,"projects":[{"project":42}]}""")]
    [InlineData("""{"version":1,"projects":[{"project":"p","name":"n","tests":[{"name":"t","verdict":"Bogus","durationMs":1}]}]}""")]
    public void Missing_malformed_and_other_versions_read_as_null(string? content)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        if (content is not null)
        {
            Directory.CreateDirectory(repo.Root.StateDirectory);
            File.WriteAllText(Path.Combine(repo.Root.StateDirectory, "last-test-results.json"), content);
        }

        Assert.Null(LastTestResults.Read(repo.Root));
    }

    [Fact]
    public void Beyond_the_bound_failures_come_first()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        var records = Enumerable.Range(0, LastTestResults.MaxTests + 100)
            .Select(i => new TestRecord($"T{i}", TestVerdict.Passed, TimeSpan.FromMilliseconds(1)))
            .Prepend(new TestRecord("Fails", TestVerdict.Failed, TimeSpan.FromMilliseconds(2)))
            .ToList();
        LastTestResults.Write(repo.Root, [(project, "Lib.Tests", new TrxResults(records.Count - 1, 1, 0, [new TestFailure("Fails", "boom", [])]) { Records = records })]);

        var tests = Assert.Single(LastTestResults.Read(repo.Root)!).Tests;

        Assert.Equal(LastTestResults.MaxTests, tests.Count);
        Assert.Equal("Fails", tests[0].Name);
        Assert.Equal(TestVerdict.Failed, tests[0].Verdict);
    }

    [Fact]
    public void A_state_directory_that_cannot_be_written_reads_as_absent_and_writing_throws_nothing()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        Directory.CreateDirectory(Path.GetDirectoryName(repo.Root.StateDirectory)!);
        File.WriteAllText(repo.Root.StateDirectory, "blocked");
        try
        {
            LastTestResults.Write(repo.Root, []);
            Assert.Null(LastTestResults.Read(repo.Root));
        }
        finally
        {
            File.Delete(repo.Root.StateDirectory);
        }
    }
}
