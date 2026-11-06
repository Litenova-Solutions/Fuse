using Fuse.Check;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     An external writer registered as a session: its files count as its own until it ends or 4 hours pass, and the
///     registration survives an engine restart. A name or a pattern that cannot be read back is refused.
/// </summary>
public class RegisteredSessionTests
{
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;
    private static readonly RepoPath Calc = Root.PathOf("Lib/Calc.cs");
    private static readonly RepoPath Report = Root.PathOf("App/Report.cs");

    [Fact]
    public void A_changed_file_under_a_pattern_is_credited_to_the_registered_session()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations);
            edits.BeginRegistration("worker-3", ["Lib/**"]);

            var writers = edits.WritersOf([Calc, Report]);

            Assert.Equal(["registered:worker-3"], writers[Calc]);
            Assert.False(writers.ContainsKey(Report));
        });
    }

    [Fact]
    public void A_file_a_hook_session_and_a_registered_writer_share_is_credited_to_both()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations);
            edits.Record("claude:a", [Calc]);
            edits.BeginRegistration("w1", ["Lib"]);

            var writers = edits.WritersOf([Calc]);

            Assert.Equal(["claude:a", "registered:w1"], writers[Calc].Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void The_registration_survives_a_restart()
    {
        InTempFiles((sessions, registrations) =>
        {
            new SessionEdits(Root, sessions, registrations).BeginRegistration("w1", ["Lib/**", "App/*.cs"]);

            var writers = new SessionEdits(Root, sessions, registrations).WritersOf([Calc, Report]);

            Assert.Equal(["registered:w1"], writers[Calc]);
            Assert.Equal(["registered:w1"], writers[Report]);
            Assert.Single(File.ReadAllLines(registrations));
        });
    }

    [Fact]
    public void Beginning_again_replaces_the_patterns_and_restarts_the_hours()
    {
        var now = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations, () => now);
            edits.BeginRegistration("w1", ["Lib/**"]);
            now += TimeSpan.FromHours(3);
            edits.BeginRegistration("w1", ["App/**"]);
            now += TimeSpan.FromHours(3);

            var writers = edits.WritersOf([Calc, Report]);

            Assert.False(writers.ContainsKey(Calc));
            Assert.Equal(["registered:w1"], writers[Report]);
        });
    }

    [Fact]
    public void After_expiry_the_file_is_uncredited_and_the_record_is_rewritten()
    {
        var now = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        string? logged = null;
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations, () => now, message => logged = message);
            edits.BeginRegistration("w1", ["Lib/**"]);
            Assert.Single(File.ReadAllLines(registrations));
            now += TimeSpan.FromHours(4);

            var writers = edits.WritersOf([Calc]);

            Assert.False(writers.ContainsKey(Calc));
            Assert.Empty(File.ReadAllLines(registrations));
            Assert.Equal("registered session w1 expired; its files count as no session's now", logged);
        });
    }

    [Fact]
    public void Ending_uncredits_immediately_and_returns_false_the_second_time()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations);
            edits.BeginRegistration("w1", ["Lib/**"]);

            Assert.True(edits.EndRegistration("w1"));
            Assert.False(edits.WritersOf([Calc]).ContainsKey(Calc));
            Assert.False(edits.EndRegistration("w1"));
            Assert.False(edits.EndRegistration("unknown"));
        });
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a b")]
    [InlineData("")]
    public void A_name_with_a_character_a_name_cannot_hold_is_refused(string name)
    {
        InTempFiles((sessions, registrations) =>
        {
            var error = Assert.Throws<FuseException>(() => new SessionEdits(Root, sessions, registrations).BeginRegistration(name, ["Lib/**"]));
            Assert.Equal(ErrorCode.InvalidPath, error.Code);
        });
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        Assert.True(SessionEdits.IsValidRegistrationName(new string('a', SessionEdits.MaxRegistrationNameLength)));
        Assert.False(SessionEdits.IsValidRegistrationName(new string('a', SessionEdits.MaxRegistrationNameLength + 1)));
    }

    [Theory]
    [InlineData("Lib/../App")]
    [InlineData("Lib/a**b/*.cs")]
    public void A_pattern_that_cannot_be_matched_is_refused(string pattern)
    {
        InTempFiles((sessions, registrations) =>
        {
            var error = Assert.Throws<FuseException>(() => new SessionEdits(Root, sessions, registrations).BeginRegistration("w1", [pattern]));
            Assert.Equal(ErrorCode.InvalidPath, error.Code);
        });
    }

    [Fact]
    public void More_patterns_or_registrations_than_the_bounds_hold_are_refused()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations);
            var tooMany = Assert.Throws<FuseException>(() => edits.BeginRegistration("w1", Enumerable.Range(0, 33).Select(i => $"Lib{i}/**").ToArray()));
            Assert.Equal(ErrorCode.InvalidPath, tooMany.Code);
            for (var i = 0; i < SessionEdits.MaxLiveRegistrations; i++)
                edits.BeginRegistration($"w{i}", ["Lib/**"]);
            var full = Assert.Throws<FuseException>(() => edits.BeginRegistration("one-more", ["Lib/**"]));
            Assert.Equal(ErrorCode.InvalidPath, full.Code);
            // Replacing a live name still works when full.
            edits.BeginRegistration("w0", ["App/**"]);
        });
    }

    [Fact]
    public void A_registration_tied_to_a_process_that_has_exited_is_uncredited_and_the_record_is_rewritten()
    {
        string? logged = null;
        InTempFiles((sessions, registrations) =>
        {
            var running = true;
            var edits = new SessionEdits(Root, sessions, registrations, processIsRunning: _ => running, log: message => logged = message);
            edits.BeginRegistration("w1", ["Lib/**"], pid: 4242);
            Assert.Equal(["registered:w1"], edits.WritersOf([Calc])[Calc]);

            running = false;
            var writers = edits.WritersOf([Calc]);

            Assert.False(writers.ContainsKey(Calc));
            Assert.Empty(File.ReadAllLines(registrations));
            Assert.Equal("registered session w1 ended; its process exited, so its files count as no session's now", logged);
        });
    }

    [Fact]
    public void A_registration_tied_to_a_running_process_stays_credited_until_it_ends()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations, processIsRunning: _ => true);
            edits.BeginRegistration("w1", ["Lib/**"], pid: 4242);

            Assert.Equal(["registered:w1"], edits.WritersOf([Calc])[Calc]);
            Assert.True(edits.EndRegistration("w1"));
            Assert.False(edits.WritersOf([Calc]).ContainsKey(Calc));
        });
    }

    [Fact]
    public void The_process_a_registration_is_tied_to_survives_a_restart()
    {
        InTempFiles((sessions, registrations) =>
        {
            new SessionEdits(Root, sessions, registrations, processIsRunning: _ => true).BeginRegistration("w1", ["Lib/**"], pid: 4242);

            var edits = new SessionEdits(Root, sessions, registrations, processIsRunning: pid => pid == 4242);
            var live = Assert.Single(edits.LiveRegistrations());

            Assert.Equal(4242, live.Pid);
            Assert.Equal(["registered:w1"], edits.WritersOf([Calc])[Calc]);
            Assert.Contains("pid:4242", File.ReadAllText(registrations), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Beginning_again_without_a_process_drops_the_process_the_registration_was_tied_to()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations, processIsRunning: _ => false);
            edits.BeginRegistration("w1", ["Lib/**"], pid: 4242);
            edits.BeginRegistration("w1", ["Lib/**"]);

            Assert.Equal(["registered:w1"], edits.WritersOf([Calc])[Calc]);
            Assert.Null(Assert.Single(edits.LiveRegistrations()).Pid);
        });
    }

    [Fact]
    public void A_process_id_that_names_no_process_ties_the_registration_to_no_process()
    {
        InTempFiles((sessions, registrations) =>
        {
            var edits = new SessionEdits(Root, sessions, registrations, processIsRunning: _ => false);
            edits.BeginRegistration("w1", ["Lib/**"], pid: 0);

            Assert.Equal(["registered:w1"], edits.WritersOf([Calc])[Calc]);
            Assert.Null(Assert.Single(edits.LiveRegistrations()).Pid);
        });
    }

    [Fact]
    public void A_line_that_does_not_read_back_is_skipped()
    {
        InTempFiles((sessions, registrations) =>
        {
            File.WriteAllLines(registrations, ["no tabs here", "noticks\tw1\tLib/**", $"{DateTimeOffset.UtcNow.AddHours(1).UtcTicks}\tw1\tLib/**"]);
            var writers = new SessionEdits(Root, sessions, registrations).WritersOf([Calc]);
            Assert.Equal(["registered:w1"], writers[Calc]);
        });
    }

    private static void InTempFiles(Action<string, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(Path.Combine(directory, "sessions.tsv"), Path.Combine(directory, "registered-sessions.tsv"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
