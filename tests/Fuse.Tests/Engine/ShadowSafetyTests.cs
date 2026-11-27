using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A test project that walks parent directories from its own build output cannot run from a shadow copy,
///     so the plan runs it from its build output instead. A helper that only names its own file, or only reads
///     a file inside the base directory, still runs from a shadow copy.
/// </summary>
public sealed class ShadowSafetyTests
{
    [Fact]
    public async Task A_helper_that_walks_up_from_the_base_directory_plans_a_build_with_the_file_named()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib.Tests/ShadowRoot.cs", """
            using System;
            using System.IO;

            namespace Lib.Tests;

            public static class ShadowRoot
            {
                public static string Root() => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.FullName;
            }
            """);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs", "Lib.Tests/ShadowRoot.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.IsType<RunMode.Build>(run.Mode);
        Assert.Contains("ShadowRoot.cs", run.ShadowSkipReason ?? "", StringComparison.Ordinal);
        Assert.Contains(engine.Log, line => line.Contains("shadow skipped for Lib.Tests: walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_helper_that_names_its_own_file_keeps_the_shadow_run()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib.Tests/CallerHelper.cs", """
            using System.Runtime.CompilerServices;

            namespace Lib.Tests;

            public static class CallerHelper
            {
                public static string OwnFile([CallerFilePath] string file = "") => file;
            }
            """);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs", "Lib.Tests/CallerHelper.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.IsType<RunMode.Shadow>(run.Mode);
        Assert.Null(run.ShadowSkipReason);
        Assert.DoesNotContain(engine.Log, line => line.Contains("shadow skipped for Lib.Tests:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_helper_that_reads_a_file_inside_the_base_directory_keeps_the_shadow_run()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib.Tests/DataHelper.cs", """
            using System;
            using System.IO;

            namespace Lib.Tests;

            public static class DataHelper
            {
                public static string Data() => Path.Combine(AppContext.BaseDirectory, "data.json");
            }
            """);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs", "Lib.Tests/DataHelper.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.IsType<RunMode.Shadow>(run.Mode);
        Assert.Null(run.ShadowSkipReason);
        Assert.DoesNotContain(engine.Log, line => line.Contains("shadow skipped for Lib.Tests:", StringComparison.Ordinal));
    }
}
