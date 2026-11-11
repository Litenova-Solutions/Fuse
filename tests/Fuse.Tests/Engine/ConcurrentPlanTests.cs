using System.Text;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Plans made at the same time, in this process and with no lock between them, answer as each would alone:
///     concurrent plans select the same tests, plans for different sessions keep their own selections, and
///     plans prepared at the same time emit the generation each one selected, into copies of their own.
/// </summary>
public class ConcurrentPlanTests
{
    [Fact]
    public async Task Concurrent_plans_answer_the_same_as_a_plan_alone()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");
        var token = TestContext.Current.CancellationToken;

        var alone = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, token);
        var plans = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, token)));

        Assert.All(plans, plan =>
        {
            Assert.Equal(alone.SelectedTests, plan.SelectedTests);
            Assert.Equal(alone.TotalTests, plan.TotalTests);
            Assert.Equal(alone.Summary, plan.Summary);
            var shadow = Assert.IsType<RunMode.Shadow>(Assert.Single(plan.Runs).Mode);
            Assert.True(File.Exists(shadow.Assembly), $"no shadow assembly at {shadow.Assembly}");
            Assert.True(File.Exists(ShadowFile(plan, "Lib.dll")), "the shadow copy holds no Lib.dll");
        });
    }

    [Fact]
    public async Task Concurrent_plans_for_different_sessions_keep_their_own_selections()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Replace("Lib/Formatter.cs", "value.ToString(System.Globalization.CultureInfo.InvariantCulture)", "\"n\" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Formatter.cs");
        var token = TestContext.Current.CancellationToken;

        var (calc, formatter) = (
            engine.Planner.PlanAsync(new TestScope.Affected { LeftOut = new HashSet<RepoPath> { engine.Repo.PathOf("Lib/Formatter.cs") } }, PhaseTimes.None, token),
            engine.Planner.PlanAsync(new TestScope.Affected { LeftOut = new HashSet<RepoPath> { engine.Repo.PathOf("Lib/Calc.cs") } }, PhaseTimes.None, token));
        var plans = await Task.WhenAll(calc, formatter);

        var calcRun = Assert.Single(plans[0].Runs);
        Assert.Equal("Lib.Tests", calcRun.Name);
        Assert.Equal(1, plans[0].SelectedTests);
        Assert.Contains("CalcTests.Multiplies", calcRun.Filter, StringComparison.Ordinal);
        var formatterRun = Assert.Single(plans[1].Runs);
        Assert.Equal("App.Tests", formatterRun.Name);
        Assert.Equal(1, plans[1].SelectedTests);
        Assert.Contains("ReportTests.Formats", formatterRun.Filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plans_prepared_at_the_same_time_keep_the_generation_each_selected()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + \"first\".Length;");
        await engine.SyncAsync("Lib/Calc.cs");
        var token = TestContext.Current.CancellationToken;
        var first = await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, token);

        engine.Repo.Replace("Lib/Calc.cs", "\"first\"", "\"second\"");
        await engine.SyncAsync("Lib/Calc.cs");
        var second = await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, token);

        var plans = await Task.WhenAll(
            Task.Run(async () => await engine.Planner.PrepareAsync(first, PhaseTimes.None, token)),
            Task.Run(async () => await engine.Planner.PrepareAsync(second, PhaseTimes.None, token)));

        var firstLib = ShadowFile(plans[0], "Lib.dll");
        var secondLib = ShadowFile(plans[1], "Lib.dll");
        Assert.NotEqual(Path.GetDirectoryName(firstLib), Path.GetDirectoryName(secondLib));
        Assert.True(HoldsString(File.ReadAllBytes(firstLib), "first"), "the first plan's Lib.dll does not hold its generation's string");
        Assert.False(HoldsString(File.ReadAllBytes(firstLib), "second"), "the first plan's Lib.dll holds the later generation's string");
        Assert.True(HoldsString(File.ReadAllBytes(secondLib), "second"), "the second plan's Lib.dll does not hold its generation's string");
        Assert.False(HoldsString(File.ReadAllBytes(secondLib), "first"), "the second plan's Lib.dll holds the earlier generation's string");
    }

    /// <summary>The file named <paramref name="name"/> next to the single shadow run's test assembly.</summary>
    private static string ShadowFile(TestPlanResult plan, string name)
    {
        var shadow = Assert.IsType<RunMode.Shadow>(Assert.Single(plan.Runs).Mode);
        return Path.Combine(Path.GetDirectoryName(shadow.Assembly)!, name);
    }

    /// <summary>Whether an assembly holds <paramref name="text"/> as a string literal, which the metadata stores in UTF-16.</summary>
    private static bool HoldsString(byte[] assembly, string text) =>
        assembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
}
