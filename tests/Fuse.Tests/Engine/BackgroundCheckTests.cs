using System.Text.Json;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The background re-check covers each live session's files once per generation after its schedules
///     debounce, skips a session whose answer the result store already holds, and stops the run a newer
///     generation supersedes. Through the router, a registered session's files are re-checked with no
///     foreground check at all, and its answer lands as its session file.
/// </summary>
public class BackgroundCheckTests
{
    [Fact]
    public async Task A_background_run_rechecks_each_live_session_and_keeps_its_answer()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new AnswerStore(state);
            var runs = new List<string>();
            var background = NewBackground(store, () => [Session("worker-1", generation: 1)], runs, TimeSpan.Zero);
            using (background)
            {
                background.Schedule(1, TestContext.Current.CancellationToken);
                await background.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }

            Assert.Equal(["worker-1"], runs);
            Assert.True(store.TryGet("key-worker-1", out _));
            Assert.True(File.Exists(Path.Combine(state, "sessions", "worker-1.json")));
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public async Task A_session_whose_answer_is_kept_is_not_checked_again_for_the_same_generation()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new AnswerStore(state);
            var runs = new List<string>();
            var background = NewBackground(store, () => [Session("worker-1", generation: 1)], runs, TimeSpan.Zero);
            using (background)
            {
                background.Schedule(1, TestContext.Current.CancellationToken);
                await background.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                background.Schedule(1, TestContext.Current.CancellationToken);
                await background.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }

            Assert.Equal(["worker-1"], runs);
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public async Task Schedules_of_one_generation_gather_into_one_run_after_the_debounce()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new AnswerStore(state);
            var runs = new List<string>();
            var background = NewBackground(store, () => [Session("worker-1", generation: 1)], runs, TimeSpan.FromMilliseconds(300));
            using (background)
            {
                for (var i = 0; i < 5; i++)
                {
                    background.Schedule(1, TestContext.Current.CancellationToken);
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                await background.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }

            Assert.Equal(["worker-1"], runs);
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public async Task A_newer_generation_cancels_the_run_it_supersedes()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new AnswerStore(state);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<BackgroundCheck.SessionFiles> current = [Session("worker-1", generation: 1)];
            async Task Check(BackgroundCheck.SessionFiles session, CancellationToken token)
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }

            var background = new BackgroundCheck(() => current, session => "key-" + session.Session, store, Check, _ => { }, TimeSpan.Zero);
            using (background)
            {
                background.Schedule(1, TestContext.Current.CancellationToken);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                current = [];
                background.Schedule(2, TestContext.Current.CancellationToken);
                await background.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    [Fact]
    public async Task A_registered_session_is_rechecked_in_the_background_and_written_as_its_session_file()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.IsType<EngineResponse.Acknowledged>(await engine.SendAsync(new EngineRequest.BeginSession("worker-2", ["Lib/**"])));
        Assert.IsType<EngineResponse.StatusAnswered>(await engine.SendAsync(new EngineRequest.Status()));

        var file = Path.Combine(repo.Root.StateDirectory, "sessions", "worker-2.json");
        var deadline = Environment.TickCount64 + 60000;
        while (!File.Exists(file) && Environment.TickCount64 < deadline)
            await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(file), $"the background check wrote no session file; log:\n{engine.Log}");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("registered:worker-2", document.RootElement.GetProperty("session").GetString());
        Assert.Contains(document.RootElement.GetProperty("errors").EnumerateArray(), e => e.GetProperty("id").GetString() == "CS0103");
    }

    /// <summary>A background check whose run keeps one empty answer per session under its key, as the router's run does.</summary>
    private static BackgroundCheck NewBackground(AnswerStore store, Func<IReadOnlyList<BackgroundCheck.SessionFiles>> sessions, List<string> runs, TimeSpan debounce) =>
        new(
            sessions,
            session => "key-" + session.Session,
            store,
            (session, _) =>
            {
                lock (runs)
                    runs.Add(session.Session);
                store.Keep("key-" + session.Session, session.Session, session.Generation, "0123456789ab", EmptyReport());
                return Task.CompletedTask;
            },
            _ => { },
            debounce);

    private static BackgroundCheck.SessionFiles Session(string session, long generation) =>
        new(session, generation, [FixtureRepo.CheckoutRoot.PathOf("src/Fuse/Program.cs")]);

    private static CheckReport EmptyReport() => new([], 1, [], [], 0, false, 0, 0, 0);

    private static string NewStateDirectory()
    {
        var state = Path.Combine(Path.GetTempPath(), "fuse-background-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        return state;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A state directory a test could not delete is in the temp directory, which the system cleans.
        }
    }
}
