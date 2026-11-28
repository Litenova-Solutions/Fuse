using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>Large uncommitted trees stay bounded: past 500 changed files the kept answer is skipped, past 2000 touched paths the projects are evaluated again, and each check logs what each session wrote.</summary>
public class LargeTreeCheckTests
{
    [Fact]
    public async Task Past_500_changed_files_both_checks_bind_and_the_log_says_the_kept_answer_was_skipped()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var fillers = new string[501];
        for (var i = 0; i < 501; i++)
        {
            fillers[i] = $"Lib/Filler{i}.cs";
            engine.Repo.Write(fillers[i], $"// filler {i}\n");
        }

        await engine.SyncAsync(fillers);
        await engine.CheckAllAsync();
        await engine.CheckAllAsync();

        Assert.Equal(2, engine.Log.Count(l => l.Contains("check: kept answer skipped, 501 changed file(s) past 500", StringComparison.Ordinal)));
        Assert.Equal(2, engine.Log.Count(l => l.StartsWith("check: binding ", StringComparison.Ordinal)));
        Assert.DoesNotContain(engine.Log, l => l == "check: kept answer");
    }

    [Fact]
    public async Task Each_check_logs_how_many_files_each_session_wrote()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \" + name", "\"Hello, \" + name");
        engine.Repo.Replace("Lib/Formatter.cs", "=> value.ToString(System.Globalization.CultureInfo.InvariantCulture);", "=> \"v\" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);");
        engine.Sessions.Record("claude:a", [engine.Repo.PathOf("Lib/Calc.cs"), engine.Repo.PathOf("Lib/Greeting.cs")]);
        engine.Sessions.Record("claude:b", [engine.Repo.PathOf("Lib/Formatter.cs")]);

        var report = await engine.CheckAsync("Lib/Calc.cs", "Lib/Greeting.cs", "Lib/Formatter.cs");

        Assert.Empty(report.Errors);
        Assert.Contains(engine.Log, l => l.Contains("; sessions: claude:a 2 file(s), claude:b 1 file(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Past_2000_touched_paths_the_next_sync_reevaluates_and_resets()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        var generation = engine.Workspace.ConfigurationGeneration;

        // 2001 files that differ from HEAD once written: touching them grows the touched set past the bound,
        // and deleting them turns every one into churn, so the next sync re-evaluates and resets.
        var relatives = Enumerable.Range(0, 2001).Select(i => "Lib/Gen" + i + ".cs").ToList();
        for (var i = 0; i < relatives.Count; i++)
            engine.Repo.Write(relatives[i], "namespace Lib;\n\npublic static class Gen" + i + "\n{\n    public static int Value() => " + i + ";\n}\n");
        await engine.Workspace.PatchViewsAsync(relatives.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        foreach (var relative in relatives)
            File.Delete(engine.Repo.Full(relative));
        // A slow watcher can report the deletions after a sync, so sync until the touched set is bounded again.
        for (var round = 0; round < 5 && (round < 2 || engine.Workspace.PrimaryOverlay.Views.ChurnCount > 2000); round++)
            await engine.Workspace.SyncAsync([], TestContext.Current.CancellationToken);

        // When the watcher has reported the deletions, the sync reloads for them; when it has not, the churn passes the
        // bound and the sync re-evaluates. Either way the projects reload and the touched set is bounded again.
        Assert.Contains(engine.Log, l => l.StartsWith("reloading: ", StringComparison.Ordinal));
        Assert.True(engine.Workspace.ConfigurationGeneration > generation);
        Assert.True(engine.Workspace.PrimaryOverlay.Views.ChurnCount <= 2000, $"churn {engine.Workspace.PrimaryOverlay.Views.ChurnCount} passed the bound");

        engine.Repo.Replace("Lib/Calc.cs", "a * b + 1;", "a * undefinedValue;");
        Assert.Equal("CS0103", Assert.Single((await engine.CheckAsync("Lib/Calc.cs")).Errors).Error.Id);
    }
}
