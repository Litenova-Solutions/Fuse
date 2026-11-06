using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check that queues behind a held reader starts its wait and its analyzer budget when a reader takes it,
///     so queue time is reported as the gate phase and does not leave it with its analyzers already cancelled.
/// </summary>
public class DequeueDeadlineTests
{
    [Fact]
    public async Task A_check_queued_past_its_wait_still_answers_when_a_reader_takes_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new Scheduler(2);
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        SchedulerLease? otherLease = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a + Missing();");

            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            otherLease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            var queued = router.HandleAsync(
                new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: true)
                {
                    BuildId = EngineVersion.Build,
                    RequestId = "dequeue-deadline",
                    WaitMs = 150,
                },
                TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            lease.Dispose();
            lease = null;

            var response = await queued.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var answered = Assert.IsType<EngineResponse.CheckAnswered>(response);
            Assert.NotEmpty(answered.Report.Errors);
            var raw = ReadLog(log).Split('\n').Single(l => l.Contains("dequeue-deadline", StringComparison.Ordinal));
            var line = raw[(raw.IndexOf("phases ", StringComparison.Ordinal) is var at and >= 0 ? at : 0)..].TrimEnd();
            Assert.True(PhaseLine.TryParse(line, out var requestId, out var kind, out var gateHolder, out var phases), line);
            Assert.Equal("dequeue-deadline", requestId);
            Assert.Equal("CheckFiles", kind);
            Assert.Equal("CheckFiles", gateHolder);
            Assert.True(phases["gate"] > 150, line);
        }
        finally
        {
            lease?.Dispose();
            otherLease?.Dispose();
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
        }
    }

    /// <summary>What the router wrote to its log, read with write sharing while background work may still append.</summary>
    private static string ReadLog(EngineLog log)
    {
        if (!File.Exists(log.FilePath))
            return "";
        using var stream = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
