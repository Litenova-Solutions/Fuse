using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     The per-engine memory budget sheds in its fixed order as pressure rises and refuses past its ceiling,
///     and the machine budget yields the engine that has been idle the longest first.
/// </summary>
public class MemoryBudgetTests
{
    [Fact]
    public void Under_the_shed_threshold_nothing_is_shed()
    {
        var budget = new MemoryBudget(ceilingBytes: 1000, workingSet: () => 500, memoryLoad: () => 0, totalAvailable: () => 0);

        var snapshot = budget.Assess();

        Assert.False(snapshot.IsPastCeiling);
        Assert.Empty(snapshot.Shed);
        Assert.Equal(500, snapshot.WorkingSetBytes);
        Assert.Equal(1000, snapshot.CeilingBytes);
    }

    [Fact]
    public void Pressure_sheds_in_the_fixed_order()
    {
        var workingSet = 750L;
        var budget = new MemoryBudget(ceilingBytes: 1000, workingSet: () => workingSet, memoryLoad: () => 0, totalAvailable: () => 0);

        Assert.Equal([MemoryBudget.ShedStep.Background], budget.Assess().Shed);

        workingSet = 960;
        Assert.Equal(
            [MemoryBudget.ShedStep.Background, MemoryBudget.ShedStep.KeptAnswersAndCaches, MemoryBudget.ShedStep.SessionViews, MemoryBudget.ShedStep.HeadCompilations, MemoryBudget.ShedStep.IdleConeProjects],
            budget.Assess().Shed);
        Assert.False(budget.Assess().IsPastCeiling);

        workingSet = 1000;
        Assert.True(budget.Assess().IsPastCeiling);
    }

    [Fact]
    public void A_full_machine_stops_only_background_work_in_an_engine_well_under_its_ceiling()
    {
        var budget = new MemoryBudget(ceilingBytes: 100 * 1024 * 1024, workingSet: () => 1024, memoryLoad: () => 960, totalAvailable: () => 1000);

        var snapshot = budget.Assess();

        // Other programs filling the machine never make one engine drop its views or refuse its callers.
        Assert.Equal([MemoryBudget.ShedStep.Background], snapshot.Shed);
        Assert.False(snapshot.IsPastCeiling);
        Assert.Equal(960, snapshot.MemoryLoadBytes);
    }

    [Fact]
    public void The_idlest_engine_yields_first_when_the_machine_runs_short()
    {
        var engines = new[]
        {
            new MemoryBudget.EngineRecord(11, 600, TimeSpan.FromMinutes(2)),
            new MemoryBudget.EngineRecord(22, 500, TimeSpan.FromMinutes(10)),
        };

        Assert.True(MemoryBudget.ShouldExitIdlest(engines, pid: 22, totalAvailable: 1000));
        Assert.False(MemoryBudget.ShouldExitIdlest(engines, pid: 11, totalAvailable: 1000));
        Assert.False(MemoryBudget.ShouldExitIdlest(engines, pid: 22, totalAvailable: 100000));
    }

    [Fact]
    public void A_record_written_for_the_machine_reads_back()
    {
        var repositories = Path.Combine(Path.GetTempPath(), "fuse-memory-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(repositories, "one");
        try
        {
            var budget = new MemoryBudget(ceilingBytes: 1000, workingSet: () => 4321, memoryLoad: () => 0, totalAvailable: () => 0);
            budget.Record(state, TimeSpan.FromSeconds(90));

            var record = Assert.Single(MemoryBudget.ReadMachine(repositories));
            Assert.Equal(Environment.ProcessId, record.Pid);
            Assert.Equal(4321, record.WorkingSetBytes);
            Assert.Equal(TimeSpan.FromSeconds(90), record.Idle);
        }
        finally
        {
            if (Directory.Exists(repositories))
                Directory.Delete(repositories, recursive: true);
        }
    }
}
