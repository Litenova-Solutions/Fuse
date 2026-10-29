using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A launcher registers an external writer's files as a session, so a judge's stop is not blocked by the writer's
///     half-written errors, until the registration ends or expires.
/// </summary>
public class SessionProcessTests
{
    [Fact]
    public async Task A_judges_stop_is_allowed_while_a_worker_is_registered_and_blocked_after_it_ends()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            // Broken with no hook payload, as a worker Fuse never sees would.
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var begun = await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w1", "--paths", "Lib/**");
            Assert.Equal(0, begun.ExitCode);
            Assert.Contains("session w1 registered for 1 path pattern(s)", begun.Stdout, StringComparison.Ordinal);

            var allowed = await FuseProcess.RunAsync(repo.Path, Stop("judge", repo.Path), "hook", "claude", "stop");
            Assert.Equal(0, allowed.ExitCode);
            Assert.DoesNotContain("decision", allowed.Stdout, StringComparison.Ordinal);

            var ended = await FuseProcess.RunAsync(repo.Path, null, "session", "end", "w1");
            Assert.Equal(0, ended.ExitCode);
            Assert.Contains("session w1 ended", ended.Stdout, StringComparison.Ordinal);

            var blocked = await FuseProcess.RunAsync(repo.Path, Stop("judge", repo.Path), "hook", "claude", "stop");
            Assert.Contains("\"decision\":\"block\"", blocked.Stdout, StringComparison.Ordinal);
            Assert.Contains("'Add'", blocked.Stdout, StringComparison.Ordinal);

            var unknown = await FuseProcess.RunAsync(repo.Path, null, "session", "end", "w1");
            Assert.Equal(2, unknown.ExitCode);
            Assert.Contains("no registered session named w1", unknown.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task An_expired_registration_blocks_the_judges_stop_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w1", "--paths", "Lib/**")).ExitCode);
            var allowed = await FuseProcess.RunAsync(repo.Path, Stop("judge", repo.Path), "hook", "claude", "stop");
            Assert.DoesNotContain("decision", allowed.Stdout, StringComparison.Ordinal);

            var file = Path.Combine(repo.Root.StateDirectory, "registered-sessions.tsv");
            var lines = File.ReadAllLines(file).Select(l => "0" + l[l.IndexOf('\t')..]).ToArray();
            File.WriteAllLines(file, lines);

            var blocked = await FuseProcess.RunAsync(repo.Path, Stop("judge", repo.Path), "hook", "claude", "stop");
            Assert.Contains("\"decision\":\"block\"", blocked.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    private static string Stop(string session, string cwd) =>
        $$"""{"session_id":"{{session}}","hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}}}""";
}
