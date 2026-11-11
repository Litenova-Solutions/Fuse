using System.Text.Json;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The machine budget: engines record their working set in local state, the machine runs short past 90
///     percent and needs memory past 70, only the idlest engine yields, and an engine leaves after the shortened
///     idle when another engine needs memory instead of after the full idle. Status reports the memory and the
///     budget a swarm admission script reads.
/// </summary>
public class MachineBudgetTests
{
    [Fact]
    public void The_machine_runs_short_past_ninety_percent_and_needs_memory_past_seventy()
    {
        var engines = new[]
        {
            new MemoryBudget.EngineRecord(11, 400, TimeSpan.FromMinutes(1)),
            new MemoryBudget.EngineRecord(22, 350, TimeSpan.FromMinutes(1)),
        };

        Assert.False(MemoryBudget.IsMachineShort(engines, totalAvailable: 1000));
        Assert.True(MemoryBudget.MachineNeedsMemory(engines, totalAvailable: 1000));
        Assert.False(MemoryBudget.MachineNeedsMemory(engines, totalAvailable: 0));
        Assert.False(MemoryBudget.IsMachineShort(engines, totalAvailable: 0));

        var shortEngines = new[]
        {
            new MemoryBudget.EngineRecord(11, 600, TimeSpan.FromMinutes(1)),
            new MemoryBudget.EngineRecord(22, 350, TimeSpan.FromMinutes(1)),
        };
        Assert.True(MemoryBudget.IsMachineShort(shortEngines, totalAvailable: 1000));
    }

    [Fact]
    public void Only_one_engine_is_the_idlest_when_idle_times_tie()
    {
        var tied = new[]
        {
            new MemoryBudget.EngineRecord(11, 500, TimeSpan.FromMinutes(10)),
            new MemoryBudget.EngineRecord(22, 600, TimeSpan.FromMinutes(10)),
            new MemoryBudget.EngineRecord(33, 600, TimeSpan.FromMinutes(10)),
        };

        // The engine holding the most yields; between equals the highest process id yields last, so one yields.
        Assert.False(MemoryBudget.IsIdlest(tied, pid: 11));
        Assert.False(MemoryBudget.IsIdlest(tied, pid: 22));
        Assert.True(MemoryBudget.IsIdlest(tied, pid: 33));
        Assert.True(MemoryBudget.ShouldExitIdlest(tied, pid: 33, totalAvailable: 1000));
        Assert.False(MemoryBudget.ShouldExitIdlest(tied, pid: 22, totalAvailable: 1000));
        Assert.False(MemoryBudget.IsIdlest(tied, pid: 99));
    }

    [Fact]
    public void A_stale_or_invalid_record_is_left_out_of_the_machine()
    {
        var repositories = Path.Combine(Path.GetTempPath(), "fuse-machine-" + Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(repositories, "stale");
        var invalid = Path.Combine(repositories, "invalid");
        try
        {
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(invalid);
            var oldTicks = DateTime.UtcNow.AddMinutes(-2).Ticks;
            File.WriteAllText(Path.Combine(stale, LocalState.EngineMemoryFile), $"123\t100\t0\t{oldTicks}");
            File.WriteAllText(Path.Combine(invalid, LocalState.EngineMemoryFile), $"0\t-1\t0\t{DateTime.UtcNow.Ticks}");

            Assert.Empty(MemoryBudget.ReadMachine(repositories));
        }
        finally
        {
            if (Directory.Exists(repositories))
                Directory.Delete(repositories, recursive: true);
        }
    }

    [Fact]
    public void The_shortened_idle_leaves_minutes_after_the_last_request_only_when_no_request_runs()
    {
        var time = new ManualClock();
        var clock = new IdleClock(time);

        time.Advance(IdleClock.ExitAfterShortened - TimeSpan.FromSeconds(1));
        Assert.False(clock.ShouldExitShortened);
        Assert.False(clock.ShouldExitWhen(machineNeedsMemory: true));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(clock.ShouldExitShortened);
        Assert.True(clock.ShouldExitWhen(machineNeedsMemory: true));
        Assert.False(clock.ShouldExit);
        Assert.False(clock.ShouldExitWhen(machineNeedsMemory: false));

        clock.Begin();
        Assert.False(clock.ShouldExitShortened);
        clock.End();
        time.Advance(IdleClock.ExitAfter);
        Assert.True(clock.ShouldExit);
        Assert.Equal(TimeSpan.FromMinutes(5), IdleClock.ExitAfterShortened);
        Assert.Equal(TimeSpan.FromMinutes(30), IdleClock.ExitAfter);
    }

    [Fact]
    public async Task Status_json_without_an_engine_reports_no_memory_and_names_the_budget_limits()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var result = await StatusOperation.RunAsync(repo.Root, true, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Text);
        var root = document.RootElement;
        Assert.Equal("stopped", root.GetProperty("engine").GetString());
        Assert.False(root.TryGetProperty("memoryWorkingSetBytes", out _));
        Assert.False(root.TryGetProperty("memoryCeilingBytes", out _));
        Assert.False(root.TryGetProperty("memoryLoadBytes", out _));
        Assert.Equal(5, StatusOperation.ShortenedIdleMinutes);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan by) => _now += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
