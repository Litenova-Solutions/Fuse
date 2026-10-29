using Fuse.Engine;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Once the tree stays unchanged for the settle interval, the engine selects the affected tests and emits them, so
///     the next plan reuses the shadow runs; edits during the window prepare nothing.
/// </summary>
public class SettledPlannerTests
{
    [Fact]
    public async Task Quiet_after_an_edit_prepares_the_shadow_run_a_plan_reuses()
    {
        await using var engine = await InProcessEngine.StartAsync();
        using var gate = new RequestGate();
        using var shutdown = new CancellationTokenSource();
        var log = new List<string>();
        void Note(string line)
        {
            lock (log)
                log.Add(line);
        }

        var settled = new SettledPlanner(engine.Planner, gate, engine.Workspace, Note, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        settled.Start(shutdown.Token);
        try
        {
            engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            await engine.SyncAsync("Lib/Calc.cs");
            var line = await WaitForLineAsync(log, "settled plan: prepared", TimeSpan.FromSeconds(30));
            Assert.Contains("1 test project(s)", line, StringComparison.Ordinal);
            var emits = engine.Planner.Emitter.Emits;
            Assert.True(emits > 0);

            var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
            Assert.NotEmpty(plan.Runs);
            Assert.Equal(emits, engine.Planner.Emitter.Emits);
        }
        finally
        {
            await shutdown.CancelAsync();
            await settled.WaitAsync();
        }
    }

    [Fact]
    public async Task Edits_during_the_window_prepare_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        using var gate = new RequestGate();
        using var shutdown = new CancellationTokenSource();
        var log = new List<string>();
        void Note(string line)
        {
            lock (log)
                log.Add(line);
        }

        // Five quiet seconds would prepare; the second edit lands after half a second.
        var settled = new SettledPlanner(engine.Planner, gate, engine.Workspace, Note, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        settled.Start(shutdown.Token);
        try
        {
            engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            await engine.SyncAsync("Lib/Calc.cs");
            await Task.Delay(500, TestContext.Current.CancellationToken);
            engine.Repo.Replace("Lib/Calc.cs", "a * b + 1;", "a * b + 2;");
            await engine.SyncAsync("Lib/Calc.cs");
            await Task.Delay(500, TestContext.Current.CancellationToken);

            lock (log)
                Assert.DoesNotContain(log, l => l.StartsWith("settled plan: prepared", StringComparison.Ordinal));
            Assert.Equal(0, engine.Planner.Emitter.Emits);
        }
        finally
        {
            await shutdown.CancelAsync();
            await settled.WaitAsync();
        }
    }

    [Fact]
    public async Task A_second_quiet_window_prepares_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        using var gate = new RequestGate();
        using var shutdown = new CancellationTokenSource();
        var log = new List<string>();
        void Note(string line)
        {
            lock (log)
                log.Add(line);
        }

        var settled = new SettledPlanner(engine.Planner, gate, engine.Workspace, Note, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        settled.Start(shutdown.Token);
        try
        {
            engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            await engine.SyncAsync("Lib/Calc.cs");
            await WaitForLineAsync(log, "settled plan: prepared", TimeSpan.FromSeconds(30));

            engine.Repo.Replace("Lib/Calc.cs", "a * b + 1;", "a * b + 2;");
            await engine.SyncAsync("Lib/Calc.cs");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                lock (log)
                {
                    if (log.Count(l => l.StartsWith("settled plan: prepared", StringComparison.Ordinal)) >= 2)
                        break;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            lock (log)
                Assert.Equal(2, log.Count(l => l.StartsWith("settled plan: prepared", StringComparison.Ordinal)));
        }
        finally
        {
            await shutdown.CancelAsync();
            await settled.WaitAsync();
        }
    }

    /// <summary>The first settled-planner line starting with <paramref name="start"/>, waiting up to <paramref name="timeout"/>.</summary>
    private static async Task<string> WaitForLineAsync(List<string> log, string start, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (log)
            {
                var line = log.FirstOrDefault(l => l.StartsWith(start, StringComparison.Ordinal));
                if (line is not null)
                    return line;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        List<string> lines;
        lock (log)
            lines = [.. log];
        Assert.Contains(lines, l => l.StartsWith(start, StringComparison.Ordinal));
        return lines.First(l => l.StartsWith(start, StringComparison.Ordinal));
    }
}
