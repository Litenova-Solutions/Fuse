using System.Text;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A test plan prepares its shadow runs after the request lock is released, from the snapshot its selection took, and
///     keeps the emitted assemblies between plans, so an unchanged project is not emitted again.
/// </summary>
public class ShadowPreparationTests
{
    [Fact]
    public async Task A_plan_whose_snapshot_predates_an_edit_emits_the_snapshot_not_the_edit()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + \"snapshot\".Length;");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var pending = await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        // The edit lands after the selection took its snapshot and before the emit, as a check from another client would.
        engine.Repo.Replace("Lib/Calc.cs", "\"snapshot\"", "\"edited\"");
        var plan = await engine.Planner.PrepareAsync(pending, PhaseTimes.None, TestContext.Current.CancellationToken);

        var lib = File.ReadAllBytes(ShadowFile(plan, "Lib.dll"));
        Assert.True(HoldsString(lib, "snapshot"), "the emitted Lib.dll does not hold the snapshot's string");
        Assert.False(HoldsString(lib, "edited"), "the emitted Lib.dll holds the edit made after the snapshot");
    }

    [Fact]
    public async Task A_second_plan_with_no_change_in_between_emits_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var emits = engine.Planner.Emitter.Emits;
        var lib = ShadowFile(first, "Lib.dll");
        var written = File.GetLastWriteTimeUtc(lib);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        // Lib and Lib.Tests are emitted once; the second plan neither emits them again nor copies build output over them.
        Assert.Equal(2, emits);
        Assert.Equal(emits, engine.Planner.Emitter.Emits);
        Assert.Equal(lib, ShadowFile(second, "Lib.dll"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(lib));
        Assert.NotEqual(File.GetLastWriteTimeUtc(engine.Repo.Full("Lib/bin/Debug/net10.0/Lib.dll")), written);
    }

    [Fact]
    public async Task A_project_changed_between_two_plans_is_emitted_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + \"first\".Length;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var emits = engine.Planner.Emitter.Emits;

        engine.Repo.Replace("Lib/Calc.cs", "\"first\"", "\"second\"");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        // Lib changed, and Lib.Tests depends on it, so both are emitted again.
        Assert.Equal(emits + 2, engine.Planner.Emitter.Emits);
        var lib = File.ReadAllBytes(ShadowFile(second, "Lib.dll"));
        Assert.Equal(ShadowFile(first, "Lib.dll"), ShadowFile(second, "Lib.dll"));
        Assert.True(HoldsString(lib, "second"), "the shadow Lib.dll does not hold the second edit");
        Assert.False(HoldsString(lib, "first"), "the shadow Lib.dll still holds the first edit");
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
