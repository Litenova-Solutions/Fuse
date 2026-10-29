using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     How an affected run with recorded failures stages them first: the first stage's failures print while the rest
///     still runs, and the answer counts each test once.
/// </summary>
public class LikelyFirstRunTests
{
    [Fact]
    public async Task The_last_failures_print_before_the_rest_runs_and_count_once()
    {
        using var repo = FixtureRepo.CreateStandard();
        var cancellationToken = TestContext.Current.CancellationToken;
        repo.Replace("Lib/Calc.cs", "a * b", "a - b");
        var whole = new TestRun(repo.Full("Lib.Tests/Lib.Tests.csproj"), "Lib.Tests", new TestRunMode.Build(), null, false, null, [], []);
        var seed = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([whole], 3, 4, "seed"), Environment.TickCount64, cancellationToken);
        Assert.Equal(Outcome.ProblemsFound, seed.Outcome);
        Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", seed.Text, StringComparison.Ordinal);

        repo.Write("Lib.Tests/SlowTests.cs", """
            namespace Lib.Tests;

            public class SlowTests
            {
                [Fact]
                public void Sleeps() => Thread.Sleep(5000);
            }
            """);
        var filter = TestFilter.For(new TestSelection.Methods(["Lib.Tests.CalcTests.Multiplies", "Lib.Tests.SlowTests.Sleeps"]));
        var run = whole with { Filter = filter.Expression };
        var plan = new TestPlan([run], 2, 5, "ran 2 test(s)");
        var failedFirst = FailedFirstSplit.ForPlan(repo.Root, plan)!;
        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], failedFirst[run.Project]);

        var live = new SyncWriter();
        var task = TestOperation.RunPlanAsync(repo.Root, plan, Environment.TickCount64, cancellationToken, live: live, failedFirstByProject: failedFirst);
        var found = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            if (live.Snapshot().Contains("FAILED Lib.Tests.CalcTests.Multiplies", StringComparison.Ordinal))
            {
                found = true;
                break;
            }

            if (task.IsCompleted)
                break;
            await Task.Delay(200, cancellationToken);
        }

        if (!found)
        {
            // Let the run finish so no process outlives the test, then fail with its answer.
            var finished = await task;
            Assert.True(found, $"the first stage's failure printed while the round still ran; the answer was: {finished.Text}");
        }

        Assert.False(task.IsCompleted);
        var staged = await task;
        Assert.Equal(Outcome.ProblemsFound, staged.Outcome);
        Assert.DoesNotContain("FAILED", staged.Text, StringComparison.Ordinal);
        Assert.StartsWith("fuse: 1 failed, 1 passed in ", staged.Text, StringComparison.Ordinal);

        var combined = await TestOperation.RunPlanAsync(
            repo.Root, plan, Environment.TickCount64, cancellationToken, failedFirstByProject: FailedFirstSplit.ForPlan(repo.Root, plan));
        Assert.Equal(Outcome.ProblemsFound, combined.Outcome);
        Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", combined.Text, StringComparison.Ordinal);
        Assert.Contains("1 failed, 1 passed", combined.Text, StringComparison.Ordinal);
    }

    /// <summary>A writer the test polls while the run writes to it.</summary>
    private sealed class SyncWriter : StringWriter
    {
        private readonly object _gate = new();

        public override void Write(string? value)
        {
            lock (_gate)
                base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
                base.Write(buffer, index, count);
        }

        public override void Flush()
        {
            lock (_gate)
                base.Flush();
        }

        public string Snapshot()
        {
            lock (_gate)
                return GetStringBuilder().ToString();
        }
    }
}
