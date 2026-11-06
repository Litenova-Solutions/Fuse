using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Engine;
using Fuse.Engine.Client;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The router answers through the scheduler: concurrent checks share the reader pool, a check that will not
///     start before its wait ends and a check past the memory ceiling answer Busy at once, and status and a
///     session registration answer while every reader is held.
/// </summary>
public class SchedulerEngineTests
{
    [Fact]
    public async Task Two_concurrent_checks_both_answer_through_the_reader_pool()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        repo.Replace("Lib/Formatter.cs", "value.ToString(", "(value + 0).ToString(");

        var answers = await Task.WhenAll(engine.CheckAsync(repo.Full("Lib/Calc.cs")), engine.CheckAsync(repo.Full("Lib/Formatter.cs")));

        Assert.All(answers, a => Assert.IsType<EngineResponse.CheckAnswered>(a));
    }

    [Fact]
    public async Task A_check_that_will_not_start_before_its_wait_ends_answers_busy()
    {
        using var repo = FixtureRepo.CreateStandard();
        var scheduler = new Scheduler(1);
        scheduler.RecordDuration(SchedulerLane.Command, TimeSpan.FromSeconds(10));
        var router = await StartRouterAsync(repo, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        try
        {
            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            var response = await router.HandleAsync(
                new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "busy-check", WaitMs = 100 },
                TestContext.Current.CancellationToken);

            var busy = Assert.IsType<EngineResponse.Busy>(response);
            Assert.Equal(1, busy.Position);
            Assert.True(busy.EstimatedWaitMs >= 10000, $"estimate {busy.EstimatedWaitMs} ms");
        }
        finally
        {
            lease?.Dispose();
            await StopRouterAsync(router);
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task A_check_past_the_memory_ceiling_answers_busy_instead_of_growing()
    {
        using var repo = FixtureRepo.CreateStandard();
        var budget = new MemoryBudget(ceilingBytes: 1, workingSet: () => Environment.WorkingSet, memoryLoad: () => 0, totalAvailable: () => 0);
        var router = await StartRouterAsync(repo, new Scheduler(2), budget);
        try
        {
            var response = await router.HandleAsync(
                new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "memory-busy" },
                TestContext.Current.CancellationToken);

            var busy = Assert.IsType<EngineResponse.Busy>(response);
            Assert.True(busy.Position >= 1);
            Assert.True(busy.EstimatedWaitMs > 0);
        }
        finally
        {
            await StopRouterAsync(router);
        }
    }

    [Fact]
    public async Task Status_and_registration_answer_while_every_reader_is_held()
    {
        using var repo = FixtureRepo.CreateStandard();
        var scheduler = new Scheduler(1);
        var router = await StartRouterAsync(repo, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        try
        {
            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);

            var status = await router.HandleAsync(
                new EngineRequest.Status { BuildId = EngineVersion.Build, RequestId = "status-bypass" },
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var answered = Assert.IsType<EngineResponse.StatusAnswered>(status);
            Assert.Equal("CheckFiles", answered.Engine.GateHolder);
            Assert.Equal(router.Budget.CeilingBytes, answered.Engine.MemoryCeilingBytes);
            Assert.True(answered.Engine.WorkingSetBytes > 0);

            var registered = await router.HandleAsync(
                new EngineRequest.BeginSession("worker-1", ["Lib/**"]) { BuildId = EngineVersion.Build, RequestId = "registration-bypass" },
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.Acknowledged>(registered);
        }
        finally
        {
            lease?.Dispose();
            await StopRouterAsync(router);
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task Identical_concurrent_checks_both_answer_from_one_in_flight_check()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");

        var file = repo.Full("Lib/Calc.cs");
        var answers = await Task.WhenAll(engine.CheckAsync(file), engine.CheckAsync(file));

        var reports = answers.Select(a => Assert.IsType<EngineResponse.CheckAnswered>(a).Report).ToList();
        Assert.All(reports, r => Assert.Contains(r.Errors, e => e.Error.Id == "CS0103"));
        Assert.Equal(reports[0].ErrorCount, reports[1].ErrorCount);
    }

    [Fact]
    public async Task The_client_retries_a_busy_answer_within_its_wait()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new BusyEngine(repo.Root, busyAnswers: 2);

        var started = Stopwatch.GetTimestamp();
        var response = await EngineClient.SendAsync(repo.Root, new EngineRequest.Ping(), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.IsType<EngineResponse.Acknowledged>(response);
        Assert.Equal(3, engine.Requests);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(150), $"elapsed {elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task The_client_answers_busy_when_its_wait_runs_out()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new BusyEngine(repo.Root, busyAnswers: 100);

        var response = await EngineClient.SendAsync(repo.Root, new EngineRequest.Ping(), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        var unanswered = Assert.IsType<EngineResponse.Unanswered>(response);
        Assert.Equal(Fuse.Failures.ErrorCode.Busy, unanswered.Code);
        Assert.Contains("the engine is busy", unanswered.Message, StringComparison.Ordinal);
    }

    private static async Task<RequestRouter> StartRouterAsync(FixtureRepo repo, Scheduler scheduler, MemoryBudget budget)
    {
        var log = new EngineLog(repo.Root.StateDirectory);
        var router = new RequestRouter(repo.Root, log, scheduler, budget);
        await router.InitializeAsync(CancellationToken.None);
        return router;
    }

    private static async Task StopRouterAsync(RequestRouter router)
    {
        await router.WaitForPreloadAsync();
        router.Dispose();
    }

    /// <summary>A pipe server under the root's engine pipe name that answers Busy a set number of times, then Acknowledged.</summary>
    private sealed class BusyEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly int _busyAnswers;
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public BusyEngine(Fuse.Paths.RepoRoot root, int busyAnswers)
        {
            _busyAnswers = busyAnswers;
            var first = NewServer(root);
            _serving = ServeAsync(root, first, _stop.Token);
        }

        private static NamedPipeServerStream NewServer(Fuse.Paths.RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task ServeAsync(Fuse.Paths.RepoRoot root, NamedPipeServerStream pipe, CancellationToken stop)
        {
            var busy = ProtocolJson.Serialize(new EngineResponse.Busy(1, 100));
            const string answer = """{"status":"Acknowledged"}""";
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    await PipeFraming.ReadLineAsync(pipe, stop);
                    var seen = Interlocked.Increment(ref _requests);
                    var line = seen <= _busyAnswers ? busy : answer;
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop);
                    await pipe.FlushAsync(stop);
                    await pipe.DisposeAsync();
                    pipe = next;
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    await pipe.DisposeAsync();
                    if (stop.IsCancellationRequested)
                        return;
                    pipe = NewServer(root);
                }
            }

            await pipe.DisposeAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }
}
