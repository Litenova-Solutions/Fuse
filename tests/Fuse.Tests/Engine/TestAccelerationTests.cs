using Fuse.Dotnet.Processes;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace Fuse.Tests.Engine;

/// <summary>
///     The test acceleration paths: a change with an empty syntax diff selects no tests and emits nothing, a shadow
///     plan never modifies build output, a Microsoft.Testing.Platform project with build output gets a shadow run, an
///     ingested test map resolves for a symbol and persists, and SDK version strings parse.
/// </summary>
public sealed class TestAccelerationTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "fuse-accel-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    [Fact]
    public async Task Comment_only_change_selects_no_tests_and_emits_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b; // multiplies");
        await engine.SyncAsync("Lib/Calc.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal(0, plan.SelectedTests);
        Assert.Equal(0, engine.Planner.Emitter.Emits);
        Assert.Contains("same code as HEAD", plan.Summary, StringComparison.Ordinal);
        Assert.Contains(engine.Log, line => line.Contains("short-circuiting without loading dependents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shadow_plan_leaves_build_output_untouched()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");
        var before = BinStamps(engine.Repo);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Runs);
        Assert.All(plan.Runs, run => Assert.IsType<RunMode.Shadow>(run.Mode));
        var shadow = Assert.IsType<RunMode.Shadow>(plan.Runs[0].Mode);
        Assert.True(File.Exists(shadow.Assembly), $"the shadow assembly is missing: {shadow.Assembly}");

        var after = BinStamps(engine.Repo);
        Assert.Equal(before.Count, after.Count);
        foreach (var (relative, stamp) in before)
        {
            Assert.True(after.TryGetValue(relative, out var current), $"build output changed by the plan: {relative}");
            Assert.True(stamp == current, $"build output changed by the plan: {relative} went from {stamp} to {current}");
        }
    }

    [Fact]
    public async Task Platform_project_with_build_output_gets_a_shadow_run()
    {
        using var repo = CreateBuiltPlatformRepo();
        await using var engine = await InProcessEngine.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.True(run.UsesTestingPlatform);
        var shadow = Assert.IsType<RunMode.Shadow>(run.Mode);
        Assert.StartsWith(repo.Root.StateDirectory, shadow.Assembly, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Tests.dll", shadow.Assembly, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, plan.SelectedTests);
        Assert.Equal(2, plan.TotalTests);
        Assert.NotNull(run.SliceKey);
    }

    [Fact]
    public void Ingested_test_map_resolves_for_a_symbol_and_persists()
    {
        var root = FixtureRepo.CheckoutRoot;
        var map = new DynamicTestMap(_stateDir);
        var ingested = map.IngestMtpTestMapContent(
            """{"methods": {"Lib.Calc.Add": ["Platform.Tests.CalcTests.Adds"]}}""",
            "Tests/Tests.csproj",
            root);

        Assert.Equal(1, ingested);
        Assert.True(map.HasAnyMapping);
        Assert.Single(Directory.GetFiles(DynamicTestMap.DirectoryFor(_stateDir), "*.json"));
        var method = AddMethod();
        Assert.True(map.TryResolveForSymbol(method, "Lib/Calc.cs", root, out var byProject));
        Assert.Equal("Platform.Tests.CalcTests.Adds", Assert.Single(Assert.Single(byProject).Value));

        var cold = new DynamicTestMap(_stateDir);
        Assert.True(cold.TryResolveForSymbol(method, "Lib/Calc.cs", root, out var reloaded));
        Assert.Equal("Platform.Tests.CalcTests.Adds", Assert.Single(Assert.Single(reloaded).Value));
    }

    [Fact]
    public void Sdk_version_string_parses_releases_previews_and_garbage()
    {
        var eleven = SdkCapabilities.FromVersionString("11.0.100");
        Assert.Equal(11, eleven.Major);
        Assert.Equal(0, eleven.Minor);
        Assert.True(eleven.SupportsMultithreadedBuild);
        Assert.True(eleven.SupportsMtpMaxFailedTests);
        Assert.True(eleven.SupportsMtpAffectedTests);

        var tenPreview = SdkCapabilities.FromVersionString("10.0.100-rc.2.25502.107");
        Assert.Equal(10, tenPreview.Major);
        Assert.Equal(0, tenPreview.Minor);
        Assert.Equal("10.0.100-rc.2.25502.107", tenPreview.RawVersion);
        Assert.False(tenPreview.SupportsMultithreadedBuild);
        Assert.False(tenPreview.SupportsMtpMaxFailedTests);
        Assert.False(tenPreview.SupportsMtpAffectedTests);

        Assert.Equal(SdkCapabilities.Unknown, SdkCapabilities.FromVersionString("not a version"));
        Assert.Equal(SdkCapabilities.Unknown, SdkCapabilities.FromVersionString(""));
        Assert.Equal(SdkCapabilities.Unknown, SdkCapabilities.FromVersionString(null));
    }

    /// <summary>Length and write time of every file under a <c>bin</c> directory of <paramref name="repo"/>, by relative path.</summary>
    private static Dictionary<string, (long Length, long Ticks)> BinStamps(FixtureRepo repo)
    {
        var stamps = new Dictionary<string, (long Length, long Ticks)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(repo.Root.Path, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo.Root.Path, file);
            if (!relative.Split(Path.DirectorySeparatorChar).Contains("bin"))
                continue;
            var info = new FileInfo(file);
            stamps[relative] = (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        Assert.NotEmpty(stamps);
        return stamps;
    }

    /// <summary>The platform template for xUnit v3 restored and built, so its test project has build output.</summary>
    private static FixtureRepo CreateBuiltPlatformRepo()
    {
        var repo = FixtureRepo.CreateEmpty(FixtureTemplate.PlatformRepo("xunit-v3", optIn: true));
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q");
        FixtureRepo.Run(repo.Root.Path, "dotnet", "build", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q", "--no-restore");
        return repo;
    }

    /// <summary>The <c>Add</c> method of a <c>Lib.Calc</c> compiled from source, resolved against the ingested map.</summary>
    private static IMethodSymbol AddMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace Lib; public class Calc { public int Add(int a, int b) => a + b; }");
        var compilation = CSharpCompilation.Create(
            "TestMapProbe",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetTypeByMetadataName("Lib.Calc")!.GetMembers("Add").OfType<IMethodSymbol>().Single();
    }
}
