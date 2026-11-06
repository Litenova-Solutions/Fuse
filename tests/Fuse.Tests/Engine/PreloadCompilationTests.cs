using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The background load of dependents loads their projects without building their compilations, so a later check
///     builds what it binds, and a signature edit still reports the errors in those dependents.
/// </summary>
public class PreloadCompilationTests
{
    [Fact]
    public async Task A_background_load_loads_the_dependent_without_building_its_compilation()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");
        using var gate = new RequestGate();
        var preloader = new Preloader(engine.Workspace, gate, engine.Workspace.Log);
        preloader.Schedule(CancellationToken.None);
        try
        {
            await WaitForLogAsync(engine, "loaded App in ");
            await preloader.WaitAsync();

            var app = engine.Workspace.Graph.Find(engine.Repo.PathOf("App/App.csproj"))!;
            Assert.True(engine.Workspace.IsLoaded(app));
            lock (engine.Log)
                Assert.DoesNotContain(engine.Log, line => line.Contains("preloaded", StringComparison.Ordinal));
            var report = await engine.CheckAsync("Lib/Calc.cs");
            Assert.Empty(report.Errors);
        }
        finally
        {
            await preloader.WaitAsync();
        }
    }

    [Fact]
    public async Task A_signature_edit_after_the_load_still_reports_the_broken_caller()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");
        using var gate = new RequestGate();
        var preloader = new Preloader(engine.Workspace, gate, engine.Workspace.Log);
        preloader.Schedule(CancellationToken.None);
        await WaitForLogAsync(engine, "loaded App in ");
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
