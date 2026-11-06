using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A registration tied to a process: while that process runs the writer's errors are another session's, and
///     once it has exited the registration has ended, checked as a check reads the registrations, with the 4-hour
///     expiry left as the fallback.
/// </summary>
public class SessionLeaseTests
{
    [Fact]
    public async Task While_the_process_runs_the_writers_errors_are_left_out_of_a_sessions_stop()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // Fold the edit in by naming it: a check of every change follows the file watcher, which may not have
        // reported a file written milliseconds ago, while a check of named files reads them from disk at once.
        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));

        var begun = await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/**"]) { Pid = Environment.ProcessId });
        var stop = Assert.IsType<EngineResponse.CheckAnswered>(
            await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { Session = "claude:judge", IsStopping = true })).Report;

        Assert.IsType<EngineResponse.Acknowledged>(begun);
        Assert.Empty(stop.Errors);
        Assert.True(stop.LeftToOtherSessions > 0);
    }

    [Fact]
    public async Task Once_the_process_has_exited_the_registration_has_ended_and_the_errors_are_told()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // Fold the edit in by naming it: a check of every change follows the file watcher, which may not have
        // reported a file written milliseconds ago, while a check of named files reads them from disk at once.
        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));

        // No process holds this id, so the process the registration is tied to has exited.
        var begun = await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/**"]) { Pid = int.MaxValue });
        var stop = Assert.IsType<EngineResponse.CheckAnswered>(
            await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { Session = "claude:judge", IsStopping = true })).Report;
        var ended = await engine.SendAsync(new EngineRequest.EndSession("w1"));

        Assert.IsType<EngineResponse.Acknowledged>(begun);
        Assert.NotEmpty(stop.Errors);
        Assert.Equal(0, stop.LeftToOtherSessions);
        var refused = Assert.IsType<EngineResponse.Unanswered>(ended);
        Assert.Equal(ErrorCode.NoRegisteredSession, refused.Code);
        Assert.Equal("no registered session named w1", refused.Message);
    }
}
