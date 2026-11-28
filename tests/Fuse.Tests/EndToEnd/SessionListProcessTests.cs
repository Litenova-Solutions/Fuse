using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.EndToEnd;

/// <summary>
///     <c>fuse session list</c> names the live session registrations, and <c>fuse status</c> shows them, until they end
///     or the process they are tied to exits.
/// </summary>
public class SessionListProcessTests
{
    [Fact]
    public async Task List_names_both_sessions_and_status_shows_them_until_one_ends()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var first = await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w1", "--paths", "Lib/**");
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("session w1 registered for 1 path pattern(s)", first.Stdout, StringComparison.Ordinal);
            var second = await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w-docs", "--paths", "docs/**");
            Assert.Equal(0, second.ExitCode);
            Assert.Contains("session w-docs registered for 1 path pattern(s)", second.Stdout, StringComparison.Ordinal);

            var list = await FuseProcess.RunAsync(repo.Path, null, "session", "list");
            Assert.Equal(0, list.ExitCode);
            Assert.Contains("w1  pid=none  paths=Lib/**  expires=", list.Stdout, StringComparison.Ordinal);
            Assert.Contains("w-docs  pid=none  paths=docs/**  expires=", list.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("no active sessions", list.Stdout, StringComparison.Ordinal);

            var json = await FuseProcess.RunAsync(repo.Path, null, "session", "list", "--json");
            Assert.Equal(0, json.ExitCode);
            using var document = JsonDocument.Parse(json.Stdout);
            var names = document.RootElement.EnumerateArray().Select(entry => entry.GetProperty("name").GetString()).ToList();
            Assert.Equal(2, names.Count);
            Assert.Contains("w1", names);
            Assert.Contains("w-docs", names);

            var status = await FuseProcess.RunAsync(repo.Path, null, "status");
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("2 active session(s): w1, w-docs", status.Stdout, StringComparison.Ordinal);
            Assert.Contains("fuse: session w1  pid=none  paths=Lib/**  expires=", status.Stdout, StringComparison.Ordinal);
            Assert.Contains("fuse: session w-docs  pid=none  paths=docs/**  expires=", status.Stdout, StringComparison.Ordinal);

            var statusJson = await FuseProcess.RunAsync(repo.Path, null, "status", "--json");
            Assert.Equal(0, statusJson.ExitCode);
            using var statusDocument = JsonDocument.Parse(statusJson.Stdout);
            var statusNames = statusDocument.RootElement.GetProperty("sessions").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()).ToList();
            Assert.Equal(2, statusNames.Count);
            Assert.Contains("w1", statusNames);
            Assert.Contains("w-docs", statusNames);

            var ended = await FuseProcess.RunAsync(repo.Path, null, "session", "end", "w1");
            Assert.Equal(0, ended.ExitCode);
            var after = await FuseProcess.RunAsync(repo.Path, null, "session", "list");
            Assert.Equal(0, after.ExitCode);
            Assert.DoesNotContain("w1  pid=", after.Stdout, StringComparison.Ordinal);
            Assert.Contains("w-docs  pid=none  paths=docs/**  expires=", after.Stdout, StringComparison.Ordinal);

            var rejected = await FuseProcess.RunAsync(repo.Path, null, "session", "list", "--bogus");
            Assert.Equal(2, rejected.ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_session_tied_to_an_exited_process_is_not_listed()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var live = await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w-live", "--paths", "Lib/**");
            Assert.Equal(0, live.ExitCode);
            var gone = await FuseProcess.RunAsync(repo.Path, null, "session", "begin", "w-gone", "--pid", ExitedProcessId().ToString(CultureInfo.InvariantCulture), "--paths", "Lib/**");
            Assert.Equal(0, gone.ExitCode);

            var running = await FuseProcess.RunAsync(repo.Path, null, "session", "list");
            Assert.Equal(0, running.ExitCode);
            Assert.Contains("w-live  pid=none", running.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("w-gone", running.Stdout, StringComparison.Ordinal);

            await FuseProcess.StopEngineAsync(repo.Root);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(60)));
            var stopped = await FuseProcess.RunAsync(repo.Path, null, "session", "list");
            Assert.Equal(0, stopped.ExitCode);
            Assert.Contains("w-live  pid=none  paths=Lib/**  expires=", stopped.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("w-gone", stopped.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("w-gone", string.Join("\n", File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "registered-sessions.tsv"))), StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>The id of a process that has already exited, which a session tied to it outlives.</summary>
    private static int ExitedProcessId()
    {
        var probe = Process.Start("dotnet", "--version");
        Assert.NotNull(probe);
        probe.WaitForExit();
        var id = probe.Id;
        probe.Dispose();
        return id;
    }
}
