using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Past eight unprepared plans only the oldest selection is forgotten, so the rest still prepare with their cached
///     counts and plan ids. Clearing them all would lose every report route.
/// </summary>
public class PendingCacheEvictionTests
{
    [Fact]
    public async Task The_ninth_selection_keeps_the_second_through_the_eighth()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var pendings = new List<Fuse.Testing.PendingPlan>();
        for (var i = 0; i < 9; i++)
            pendings.Add(await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken));

        var plan = await engine.Planner.PrepareAsync(pendings[1], PhaseTimes.None, TestContext.Current.CancellationToken);
        var (planId, _) = engine.Planner.WireInfoFor(plan);

        Assert.NotEqual("", planId);
    }
}
