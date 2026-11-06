using Fuse.Graph;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The stored project graph, keyed by the project files and covering every import, lets a fresh engine
///     answer without evaluating again: the stored graph answers as the evaluated one does, and a changed
///     project file, a changed or appearing import, or an unreadable stored graph evaluates again.
/// </summary>
public class GraphCacheTests
{
    [Fact]
    public async Task A_stored_graph_answers_as_the_evaluated_graph_does()
    {
        using var repo = FixtureRepo.CreateStandard();
        var cancellationToken = TestContext.Current.CancellationToken;

        var evaluated = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        Assert.Empty(evaluated.Failures);
        Assert.NotEmpty(evaluated.Projects);
        Assert.Single(Directory.EnumerateFiles(GraphCache.DirectoryFor(repo.Root.SharedStateDirectory), "graph.json", SearchOption.AllDirectories));

        var stored = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);

        AssertGraphEqual(evaluated, stored);
        var calc = repo.PathOf("Lib/Calc.cs");
        Assert.Equal(
            evaluated.OwnersOf(calc).Select(p => p.Path.Relative).Order(StringComparer.Ordinal),
            stored.OwnersOf(calc).Select(p => p.Path.Relative).Order(StringComparer.Ordinal));
        var lib = evaluated.Find(repo.PathOf("Lib/Lib.csproj"))!;
        Assert.Equal(
            evaluated.DependentsOf(lib).Select(p => p.Path.Relative).Order(StringComparer.Ordinal),
            stored.DependentsOf(stored.Find(repo.PathOf("Lib/Lib.csproj"))!).Select(p => p.Path.Relative).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_changed_project_file_evaluates_again_and_is_stored_under_its_new_key()
    {
        using var repo = FixtureRepo.CreateStandard();
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluated = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        var paths = evaluated.Projects.Select(p => p.Path).ToList();
        Assert.NotNull(GraphCache.TryLoad(repo.Root, paths));

        repo.Write("Lib/Lib.csproj", repo.Read("Lib/Lib.csproj") + "<!-- stored graph test -->\n");

        Assert.Null(GraphCache.TryLoad(repo.Root, paths));
        var again = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        Assert.Empty(again.Failures);
        Assert.NotNull(GraphCache.TryLoad(repo.Root, paths));
        AssertGraphEqual(evaluated, again);
    }

    [Fact]
    public async Task An_appearing_or_changed_import_evaluates_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluated = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        var paths = evaluated.Projects.Select(p => p.Path).ToList();
        Assert.NotNull(GraphCache.TryLoad(repo.Root, paths));

        repo.Write("Directory.Build.props", "<Project></Project>\n");
        Assert.Null(GraphCache.TryLoad(repo.Root, paths));

        await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        Assert.NotNull(GraphCache.TryLoad(repo.Root, paths));

        repo.Write("Directory.Build.props", "<Project><!-- changed --></Project>\n");
        Assert.Null(GraphCache.TryLoad(repo.Root, paths));
    }

    [Fact]
    public async Task An_unreadable_stored_graph_evaluates_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluated = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);
        var paths = evaluated.Projects.Select(p => p.Path).ToList();
        var file = Assert.Single(Directory.EnumerateFiles(GraphCache.DirectoryFor(repo.Root.SharedStateDirectory), "graph.json", SearchOption.AllDirectories));
        File.WriteAllText(file, "not a stored graph");

        Assert.Null(GraphCache.TryLoad(repo.Root, paths));
        var again = await RepoGraph.EvaluateAsync(repo.Root, cancellationToken);

        AssertGraphEqual(evaluated, again);
        Assert.NotNull(GraphCache.TryLoad(repo.Root, paths));
    }

    [Fact]
    public void Pruning_keeps_the_two_most_recently_written_graphs()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var directory = GraphCache.DirectoryFor(repo.Root.SharedStateDirectory);
        var oldest = Directory.CreateDirectory(Path.Combine(directory, "oldest")).FullName;
        var middle = Directory.CreateDirectory(Path.Combine(directory, "middle")).FullName;
        var newest = Directory.CreateDirectory(Path.Combine(directory, "newest")).FullName;
        foreach (var (key, written) in new[] { (oldest, DateTime.UtcNow.AddDays(-3)), (middle, DateTime.UtcNow.AddDays(-2)), (newest, DateTime.UtcNow.AddDays(-1)) })
        {
            var file = Path.Combine(key, "graph.json");
            File.WriteAllText(file, "{}");
            File.SetLastWriteTimeUtc(file, written);
            Directory.SetLastWriteTimeUtc(key, written);
        }

        GraphCache.Prune(repo.Root.SharedStateDirectory);

        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
    }

    private static void AssertGraphEqual(RepoGraph expected, RepoGraph actual)
    {
        Assert.Equal(
            expected.Projects.Select(p => p.Path.Relative).Order(StringComparer.Ordinal),
            actual.Projects.Select(p => p.Path.Relative).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Failures, actual.Failures);
        foreach (var project in expected.Projects)
        {
            var stored = actual.Find(project.Path)!;
            Assert.NotNull(stored);
            Assert.Equal(project.Name, stored.Name);
            Assert.Equal(project.Directory.Relative, stored.Directory.Relative);
            Assert.Equal(project.References.Select(r => r.Relative).Order(StringComparer.Ordinal), stored.References.Select(r => r.Relative).Order(StringComparer.Ordinal));
            Assert.Equal(project.Sources.Select(s => s.Relative).Order(StringComparer.Ordinal), stored.Sources.Select(s => s.Relative).Order(StringComparer.Ordinal));
            Assert.Equal(project.NonSources.Select(s => s.Relative).Order(StringComparer.Ordinal), stored.NonSources.Select(s => s.Relative).Order(StringComparer.Ordinal));
            Assert.Equal(project.BuildOnlyReferences.Select(r => r.Relative).Order(StringComparer.Ordinal), stored.BuildOnlyReferences.Select(r => r.Relative).Order(StringComparer.Ordinal));
            Assert.Equal(project.EvaluationInputs.Select(i => i.Relative).Order(StringComparer.Ordinal), stored.EvaluationInputs.Select(i => i.Relative).Order(StringComparer.Ordinal));
            Assert.Equal(project.AssetsFile.Relative, stored.AssetsFile.Relative);
            Assert.Equal(project.IsTest, stored.IsTest);
            Assert.Equal(project.IsExecutable, stored.IsExecutable);
            Assert.Equal(project.UsesTestingPlatform, stored.UsesTestingPlatform);
            Assert.Equal(project.Framework, stored.Framework);
            Assert.Equal(project.RequiresContainerRuntime, stored.RequiresContainerRuntime);
            Assert.Equal(project.TargetPaths, stored.TargetPaths);
            Assert.Equal(project.RunSettings, stored.RunSettings);
        }
    }
}
