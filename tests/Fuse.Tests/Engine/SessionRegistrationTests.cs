using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Registering an external writer as a session through the engine: a begin is acknowledged even while projects
///     load, an end on an unknown name is refused, and while registered the writer's errors are another session's.
/// </summary>
public class SessionRegistrationTests
{
    [Fact]
    public async Task Begin_is_acknowledged_and_end_on_an_unknown_name_is_refused()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var begun = await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/**"]));
        var ended = await engine.SendAsync(new EngineRequest.EndSession("w1"));
        var unknown = await engine.SendAsync(new EngineRequest.EndSession("w1"));

        Assert.IsType<EngineResponse.Acknowledged>(begun);
        Assert.IsType<EngineResponse.Acknowledged>(ended);
        var refused = Assert.IsType<EngineResponse.Unanswered>(unknown);
        Assert.Equal(ErrorCode.NoRegisteredSession, refused.Code);
        Assert.Equal("no registered session named w1", refused.Message);
    }

    [Fact]
    public async Task A_begin_with_a_name_or_a_pattern_that_cannot_hold_is_refused()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var badName = Assert.IsType<EngineResponse.Unanswered>(await engine.SendAsync(new EngineRequest.BeginSession("a/b", ["Lib/**"])));
        var badPattern = Assert.IsType<EngineResponse.Unanswered>(await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/../App"])));

        Assert.Equal(ErrorCode.InvalidPath, badName.Code);
        Assert.Equal(ErrorCode.InvalidPath, badPattern.Code);
    }

    [Fact]
    public async Task While_registered_the_writers_errors_are_left_out_of_a_sessions_stop()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // Fold the edit in by naming it: a check of every change follows the file watcher, which may not have
        // reported a file written milliseconds ago, while a check of named files reads them from disk at once.
        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));
        await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/**"]));

        var stop = Assert.IsType<EngineResponse.CheckAnswered>(
            await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { Session = "claude:judge", IsStopping = true })).Report;

        Assert.Empty(stop.Errors);
        Assert.True(stop.LeftToOtherSessions > 0);
        await engine.SendAsync(new EngineRequest.EndSession("w1"));
        var after = Assert.IsType<EngineResponse.CheckAnswered>(
            await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { Session = "claude:judge", IsStopping = true })).Report;
        Assert.NotEmpty(after.Errors);
        Assert.Equal(0, after.LeftToOtherSessions);
    }

    [Fact]
    public async Task A_check_with_no_session_still_reports_a_registered_writers_errors()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // Fold the edit in by naming it: a check of every change follows the file watcher, which may not have
        // reported a file written milliseconds ago, while a check of named files reads them from disk at once.
        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));
        await engine.SendAsync(new EngineRequest.BeginSession("w1", ["Lib/**"]));

        var report = Assert.IsType<EngineResponse.CheckAnswered>(
            await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true))).Report;

        Assert.NotEmpty(report.Errors);
        Assert.Equal(0, report.LeftToOtherSessions);
    }
}
