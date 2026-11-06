using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

/// <summary>
///     A summary is emitted only by the background warm, for a project outside the change cone, after a
///     request and never under the writer lock. A request reads only a summary that is already kept, and a
///     summary the warm emits starts being supplied only when the views are derived from the loader again.
/// </summary>
public class BackgroundSummaryTests
{
    [Fact]
    public async Task The_background_warm_emits_a_summary_outside_the_cone_and_no_view_uses_it_until_the_views_are_derived_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("App/Report.cs");
        Assert.False(SummariesStored(engine.Repo));

        using var gate = new RequestGate();
        var warmer = new BaselineWarmer(engine.Workspace, engine.Checker, gate, engine.Workspace.Log);
        var cancellationToken = TestContext.Current.CancellationToken;
        warmer.Schedule(cancellationToken);
        await warmer.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        Assert.True(SummariesStored(engine.Repo));
        Assert.Contains(engine.Log, line => line.Contains("summary emitted: Lib", StringComparison.Ordinal));
        var app = engine.Workspace.Current.Projects.Single(p => p.Name == "App");
        var lib = engine.Workspace.Current.Projects.Single(p => p.Name == "Lib");
        Assert.Contains(app.ProjectReferences, reference => reference.ProjectId == lib.Id);

        await engine.Workspace.RebuildViewsAsync(cancellationToken);

        app = engine.Workspace.Current.Projects.Single(p => p.Name == "App");
        Assert.DoesNotContain(app.ProjectReferences, reference => reference.ProjectId == lib.Id);
        Assert.Single(app.MetadataReferences.OfType<PortableExecutableReference>(), reference => reference.FilePath is not null && reference.FilePath.Contains("summaries", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_background_warm_emits_nothing_for_a_project_in_the_change_cone()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("App/Report.cs");
        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");
        var lib = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");

        Assert.False(await engine.Workspace.EmitSummaryAsync(lib, TestContext.Current.CancellationToken));
        Assert.False(SummariesStored(engine.Repo));
    }

    [Fact]
    public async Task Summary_emission_runs_in_the_background_after_a_request_not_inside_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var answer = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(repo.Full("App/Report.cs")));
        Assert.Empty(answer.Report.Errors);

        await WaitForLogAsync(engine, "summary emitted: ");
        Assert.True(SummariesStored(repo));
    }

    /// <summary>True when the shared state directory of <paramref name="repo"/> holds a summary assembly.</summary>
    private static bool SummariesStored(FixtureRepo repo)
    {
        var directory = Path.Combine(repo.Root.SharedStateDirectory, "summaries");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();
    }

    /// <summary>Waits up to a minute for the router's log to hold <paramref name="text"/>, which the background warm writes when it emits.</summary>
    private static async Task WaitForLogAsync(InProcessRequestRouter engine, string text)
    {
        for (var i = 0; i < 600 && !engine.Log.Contains(text, StringComparison.Ordinal); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(engine.Log.Contains(text, StringComparison.Ordinal), $"the log never held \"{text}\":\n{engine.Log}");
    }
}
