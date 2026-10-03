using Fuse.Check;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The record of which sessions wrote each changed file: it survives an engine restart, credits a file to every
///     session that wrote it, forgets a file that matches HEAD again, and refuses a session id it could not read back.
/// </summary>
public class SessionEditsTests
{
    // The record names files but reads none, so any root will do.
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;
    private static readonly RepoPath Calc = Root.PathOf("Lib/Calc.cs");
    private static readonly RepoPath Report = Root.PathOf("App/Report.cs");

    [Fact]
    public void A_file_two_sessions_wrote_is_credited_to_both() =>
        InTempFile(file =>
        {
            var edits = new SessionEdits(Root, file);
            edits.Record("claude:a", [Calc]);
            edits.Record("opencode:b", [Calc, Report]);

            var writers = edits.WritersOf([Calc, Report]);

            Assert.Equal(["claude:a", "opencode:b"], writers[Calc].Order(StringComparer.Ordinal));
            Assert.Equal(["opencode:b"], writers[Report]);
        });

    [Fact]
    public void The_record_survives_a_restart() =>
        InTempFile(file =>
        {
            new SessionEdits(Root, file).Record("claude:a/agent-1", [Calc]);

            var writers = new SessionEdits(Root, file).WritersOf([Calc]);

            Assert.Equal(["claude:a/agent-1"], writers[Calc]);
            Assert.Equal([$"Lib/Calc.cs\tclaude:a/agent-1"], File.ReadAllLines(file));
        });

    [Fact]
    public void A_file_that_matches_HEAD_again_is_forgotten_on_disk_too() =>
        InTempFile(file =>
        {
            var edits = new SessionEdits(Root, file);
            edits.Record("claude:a", [Calc, Report]);

            edits.Forget([Report]);

            Assert.False(edits.WritersOf([Calc, Report]).ContainsKey(Calc));
            Assert.False(new SessionEdits(Root, file).WritersOf([Calc, Report]).ContainsKey(Calc));
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void A_session_id_that_would_not_read_back_is_refused(string? session) =>
        Assert.False(SessionEdits.IsValid(session));

    [Fact]
    public void A_session_id_longer_than_the_limit_is_refused()
    {
        Assert.True(SessionEdits.IsValid(new string('a', SessionEdits.MaxSessionLength)));
        Assert.False(SessionEdits.IsValid(new string('a', SessionEdits.MaxSessionLength + 1)));
    }

    [Fact]
    public void A_line_that_does_not_read_back_is_skipped() =>
        InTempFile(file =>
        {
            File.WriteAllLines(file, ["no tab here", "\tno path", "Lib/Calc.cs\tclaude:a", "App/Report.cs\t"]);

            var writers = new SessionEdits(Root, file).WritersOf([Calc, Report]);

            Assert.Equal(["claude:a"], writers[Calc]);
            Assert.False(writers.ContainsKey(Report));
        });

    private static void InTempFile(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(Path.Combine(directory, "sessions.tsv"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
