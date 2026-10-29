using Fuse.Engine;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Each settle poll folds the watcher's changes in through a sync, so an edit needs no earlier hook or check to be
///     seen. Without the poll sync the tracker would stay empty until something else synced.
/// </summary>
public class SettledPollSyncTests
{
    [Fact]
    public async Task An_edit_is_folded_in_without_an_explicit_sync()
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

        var settled = new SettledPlanner(engine.Planner, gate, engine.Workspace, Note, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        settled.Start(shutdown.Token);
        try
        {
            engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var changed = engine.Repo.PathOf("Lib/Calc.cs");
            while (DateTime.UtcNow < deadline)
            {
                List<Fuse.Paths.RepoPath> current;
                await gate.EnterAsync("SettledPollSyncTest", TestContext.Current.CancellationToken);
                try
                {
                    current = [.. engine.Workspace.Tracker.Changed];
                }
                finally
                {
                    gate.Exit();
                }

                if (current.Contains(changed))
                    return;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            List<Fuse.Paths.RepoPath> missing;
            await gate.EnterAsync("SettledPollSyncTest", TestContext.Current.CancellationToken);
            try
            {
                missing = [.. engine.Workspace.Tracker.Changed];
            }
            finally
            {
                gate.Exit();
            }

            Assert.Contains(changed, missing);
        }
        finally
        {
            await shutdown.CancelAsync();
            await settled.WaitAsync();
        }
    }
}
