using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>A test project that references Testcontainers runs only when a container runtime answers; without one its tests are left out of the plan, loudly.</summary>
public class ContainerGatingTests
{
    private const string TestcontainersReference = """<ItemGroup><PackageReference Include="Testcontainers.PostgreSql" Version="3.*" /></ItemGroup>""";

    /// <summary>The standard repository with Lib.Tests needing a container runtime, restored so it still loads.</summary>
    private static FixtureRepo WithGatedTests()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib.Tests/Lib.Tests.csproj", "</Project>", TestcontainersReference + "\n</Project>");
        FixtureRepo.Run(repo.Path, "dotnet", "restore", "Lib.Tests/Lib.Tests.csproj", "-nologo", "-v:q");
        return repo;
    }

    [Fact]
    public async Task Without_a_runtime_a_gated_project_has_no_run_and_the_summary_says_so()
    {
        await using var engine = await InProcessEngine.StartAsync(WithGatedTests());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var planner = new TestPlanner(engine.Workspace, containerProbe: () => false);

        var plan = await planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal(0, plan.SelectedTests);
        Assert.Equal(1, plan.ContainerGatedTests);
        Assert.Equal(["Lib.Tests"], plan.ContainerGatedProjects);
        Assert.Contains("skipped 1 test(s) in Lib.Tests (no container runtime)", plan.Summary, StringComparison.Ordinal);
        Assert.Contains(engine.Log, l => l.Contains("left out 1 test(s) in Lib.Tests (no container runtime)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_a_runtime_a_gated_project_plans_as_usual()
    {
        await using var engine = await InProcessEngine.StartAsync(WithGatedTests());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var planner = new TestPlanner(engine.Workspace, containerProbe: () => true);

        var plan = await planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Contains(plan.Runs, r => r.Name == "Lib.Tests");
        Assert.Equal(0, plan.ContainerGatedTests);
        Assert.Empty(plan.ContainerGatedProjects);
        Assert.DoesNotContain("no container runtime", plan.Summary, StringComparison.Ordinal);
        Assert.Contains(engine.Log, l => l.Contains("container runtime present; 1 gated project(s) planned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_runtime_all_tests_leaves_the_gated_project_out()
    {
        await using var engine = await InProcessEngine.StartAsync(WithGatedTests());
        var planner = new TestPlanner(engine.Workspace, containerProbe: () => false);

        var plan = await planner.PlanAsync(new TestScope.All(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(plan.Runs, r => r.Name == "Lib.Tests");
        Assert.Contains(plan.Runs, r => r.Name == "App.Tests");
        Assert.Equal(3, plan.ContainerGatedTests);
        Assert.Equal(["Lib.Tests"], plan.ContainerGatedProjects);
        Assert.Contains("skipped 3 test(s) in Lib.Tests (no container runtime)", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_answers_plans_for_five_minutes()
    {
        await using var engine = await InProcessEngine.StartAsync(WithGatedTests());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var time = new FakeTimeProvider();
        var probes = 0;
        var planner = new TestPlanner(engine.Workspace, time: time, containerProbe: () => { probes++; return false; });

        await planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        await planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Equal(1, probes);

        time.Advance(TimeSpan.FromMinutes(6));
        await planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Equal(2, probes);
    }

    /// <summary>A clock the test moves past the probe's expiry.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }
}
