using Fuse.Testing.Caching;
using Fuse.Testing.Model.Caching;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace Fuse.Tests.Engine;

/// <summary>Verifies the idle release of the token index, the slice cache, the dynamic map and the workspace caches.</summary>
public sealed class IdleReleaseTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "fuse-idle-release-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    [Fact]
    public async Task SyntaxTokenIndex_ReleaseIdle_drops_entries_and_reloads_from_disk()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Base.cs"] = "namespace App;\npublic interface IBase { void Ping(); }\n",
            ["Other.cs"] = "namespace App;\npublic sealed class Other { public int Value() => 42; }\n",
        });

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "TestProj", "TestProj", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        using var ws = new AdhocWorkspace();
        var solution = ws.CurrentSolution.AddProject(projectInfo);
        foreach (var file in Directory.EnumerateFiles(repo.Root.Path, "*.cs"))
        {
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(docId, Path.GetFileName(file), File.ReadAllText(file), filePath: file);
        }

        var index = new SyntaxTokenIndex(repo.Root);
        var ct = TestContext.Current.CancellationToken;
        await index.EnsureIndexedAsync(solution, ct);
        Assert.Equal(2, index.DocumentCount);
        index.Save("idle-test");
        Assert.True(File.Exists(Path.Combine(index.Directory, "idle-test.bin")));

        Assert.Equal(2, index.ReleaseIdle());
        Assert.Equal(0, index.DocumentCount);

        // The snapshot path never parses, so entries it finds come from disk.
        _ = index.CandidateDocumentsSnapshot(solution, ["Ping"], []);
        Assert.Equal(2, index.DocumentCount);

        var docs = index.CandidateDocuments(solution, ["Ping"], []);
        Assert.Equal(["Base.cs"], docs.Select(d => Path.GetFileName(d.FilePath)).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SliceTestCache_ReleaseIdle_drops_memory_and_reads_disk()
    {
        var cache = new SliceTestCache(_stateDir);
        var key = SliceTestCache.ComputeKey(new SliceTestCache.KeyInputs(
            "tests/App.Tests/App.Tests.csproj",
            "net10.0",
            "pe:pdb",
            [("src/Lib/Lib.csproj|net10.0", "lib")],
            [],
            "",
            SliceTestCache.HashFilter(null, [])));
        cache.Store(new SliceCacheResult(
            key,
            "tests/App.Tests/App.Tests.csproj",
            "App.Tests",
            "net10.0",
            Passed: 3,
            Failed: 1,
            Skipped: 0,
            DurationMs: 12.5,
            CachedAtUtc: new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc),
            Failures: [new SliceTestFailure("App.Tests.BrokenTest", "Expected 2, got 3", ["at App.Tests.BrokenTest()"])]));

        Assert.True(cache.ReleaseIdle() >= 1);

        var reread = cache.TryGet(key);
        Assert.NotNull(reread);
        Assert.Equal(3, reread.Passed);
        Assert.Equal(1, reread.Failed);
        Assert.Equal("App.Tests.BrokenTest", Assert.Single(reread.Failures).Name);
    }

    [Fact]
    public void DynamicTestMap_ReleaseIdle_drops_memory_and_reads_disk()
    {
        var root = FixtureRepo.CheckoutRoot;
        var map = new DynamicTestMap(_stateDir);
        var ingested = map.IngestMtpTestMapContent(
            """{"methods": {"Lib.Calc.Add": ["Platform.Tests.CalcTests.Adds"]}}""",
            "Tests/Tests.csproj",
            root);

        Assert.Equal(1, ingested);
        Assert.True(map.HasAnyMapping);
        Assert.Equal(1, map.ReleaseIdle());
        Assert.True(map.HasAnyMapping);
        Assert.Equal(1, map.ReleaseIdle());
    }

    [Fact]
    public void RepoWorkspace_ReleaseIdle_drops_head_texts()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Lib/Calc.cs"] = "public class Calc { public int Add(int a, int b) => a + b; }\n",
        });
        using var workspace = new RepoWorkspace(repo.Root, _ => { });
        var shared = workspace.PrimaryOverlay.SharedResources;
        var absolute = Path.Combine(repo.Root.Path, "Lib", "Calc.cs");

        Assert.NotNull(shared.GetOrReadHeadText("head1", "Lib/Calc.cs", absolute));
        Assert.Equal(1, shared.HeadTextCount);
        workspace.ReleaseIdle();
        Assert.Equal(0, shared.HeadTextCount);
        Assert.NotNull(shared.GetOrReadHeadText("head1", "Lib/Calc.cs", absolute));
        Assert.Equal(1, shared.HeadTextCount);
    }
}
