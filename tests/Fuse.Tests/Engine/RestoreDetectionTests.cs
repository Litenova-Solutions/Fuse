using Fuse.Failures;
using Fuse.Repo.Tracking;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A package reference added without restoring reports that a restore is needed, a restore afterwards lets the
///     next check pass without stopping the engine and reloads the loaded projects, and a git-ignored project file
///     never evaluates the projects again.
/// </summary>
public class RestoreDetectionTests
{
    [Fact]
    public async Task A_package_reference_without_a_restore_reports_that_a_restore_is_needed()
    {
        await using var engine = await InProcessEngine.StartAsync();
        AddPackageReference(engine.Repo);
        engine.Workspace.Tracker.ReportWritten([engine.Repo.PathOf("Lib/Lib.csproj")]);

        var error = await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync("Lib/Calc.cs"));

        Assert.Equal(ErrorCode.RestoreNeeded, error.Code);
        Assert.Contains("dotnet restore Lib/Lib.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restore_after_a_package_reference_lets_the_next_check_pass()
    {
        await using var engine = await InProcessEngine.StartAsync();
        AddPackageReference(engine.Repo);
        FixtureRepo.Run(engine.Repo.Root.Path, "dotnet", "restore", "Lib/Lib.csproj", "-nologo", "-v:q");
        engine.Workspace.Tracker.ReportWritten([engine.Repo.PathOf("Lib/Lib.csproj")]);

        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.Empty(report.Errors);
        Assert.Contains(engine.Log, line => line.StartsWith("reloading:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restore_that_only_rewrites_restore_output_reloads_the_loaded_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        engine.Log.Clear();
        File.Delete(engine.Repo.Full("Lib/obj/project.assets.json"));
        FixtureRepo.Run(engine.Repo.Root.Path, "dotnet", "restore", "Lib/Lib.csproj", "-nologo", "-v:q");

        await engine.SyncAsync("Lib/Calc.cs");

        Assert.Contains(engine.Log, line => line.StartsWith("reloading:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_git_ignored_project_file_does_not_reevaluate()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write(".gitignore", "bin/\nobj/\n.agents/\n");
        repo.Commit("ignore agent files");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Log.Clear();
        engine.Repo.Write(".agents/tmp/packages.lock.json", "{}\n");

        await engine.SyncAsync(".agents/tmp/packages.lock.json");

        Assert.DoesNotContain(engine.Log, line => line.StartsWith("reloading:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ignored_project_file_the_watcher_reports_is_dropped_before_reevaluating()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            [".gitignore"] = "ignored/\n",
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
            ["Lib/Calc.cs"] = "namespace Lib;\n\npublic class Calc {}\n",
        });
        var watched = new WatchedPaths(repo.Root);
        using var tracker = await StartAsync(repo, watched);
        repo.Write("ignored/extra.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        watched.Record(repo.Full("ignored/extra.csproj"), appearedOrVanished: true);

        var patch = Assert.IsType<SyncResult.Patch>(await tracker.SyncAsync([], TestContext.Current.CancellationToken));

        Assert.Empty(patch.Paths);
    }

    /// <summary>Adds a cached package reference to Lib and uses one of its types, without restoring.</summary>
    private static void AddPackageReference(FixtureRepo repo)
    {
        repo.Replace("Lib/Lib.csproj", "</Project>", """<ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup></Project>""");
        repo.Write("Lib/Calc.cs", """
            using Xunit;

            namespace Lib;

            public class Calc
            {
                public int Add(int a, int b) => a + b;

                public int Mul(int a, int b) => a * b;

                public static string FactName() => nameof(FactAttribute);
            }
            """);
    }

    private static async Task<ChangeTracker> StartAsync(FixtureRepo repo, WatchedPaths watched)
    {
        var tracker = new ChangeTracker(repo.Root, watched);
        await tracker.InitializeAsync(TestContext.Current.CancellationToken);
        return tracker;
    }
}
