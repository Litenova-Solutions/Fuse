using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

/// <summary>
///     The background load of dependents also builds their compilations, so the first check that binds them reuses what
///     is already built, and a later signature edit still reports the errors in those dependents.
/// </summary>
public class PreloadCompilationTests
{
    [Fact]
    public async Task A_background_load_also_builds_the_dependents_compilation()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");
        using var gate = new RequestGate();
        var preloader = new Preloader(engine.Workspace, gate, engine.Workspace.Log);
        preloader.Schedule(CancellationToken.None);
        try
        {
            await WaitForLogAsync(engine, "preloaded App compilation in ");
            await engine.CheckAsync("Lib/Calc.cs");

            var app = engine.Workspace.Graph.Find(engine.Repo.PathOf("App/App.csproj"))!;
            var warmed = await engine.Workspace.PreloadCompilationAsync(app, TestContext.Current.CancellationToken);
            var viewed = RepoWorkspace.ProjectsFor(engine.Workspace.Current, app).Single();
            var compilation = await viewed.GetCompilationAsync(TestContext.Current.CancellationToken);

            Assert.Single(warmed);
            Assert.Same(warmed[0], compilation);
        }
        finally
        {
            await preloader.WaitAsync();
        }
    }

    [Fact]
    public async Task A_signature_edit_after_the_warm_still_reports_the_broken_caller()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");
        using var gate = new RequestGate();
        var preloader = new Preloader(engine.Workspace, gate, engine.Workspace.Log);
        preloader.Schedule(CancellationToken.None);
        await WaitForLogAsync(engine, "preloaded App compilation in ");
        await preloader.WaitAsync();

        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        var error = Assert.Single(report.Errors, e => e.Error.Path == "App/Program.cs");
        Assert.Equal("CS1061", error.Error.Id);
        Assert.Equal(new Cause.Removed("public int Add(int a, int b)"), error.Cause);
    }

    /// <summary>Waits up to a minute for the engine log to hold <paramref name="text"/>, which a background load writes when it gets there.</summary>
    private static async Task WaitForLogAsync(InProcessEngine engine, string text)
    {
        for (var i = 0; i < 600; i++)
        {
            lock (engine.Log)
            {
                if (engine.Log.Any(l => l.Contains(text, StringComparison.Ordinal)))
                    return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        lock (engine.Log)
            Assert.Fail($"the log never held \"{text}\":\n{string.Join('\n', engine.Log)}");
    }
}
