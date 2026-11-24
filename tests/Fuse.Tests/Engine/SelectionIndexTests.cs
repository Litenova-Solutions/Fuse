using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

/// <summary>
///     Covers selection with a syntax token index, with an index that cannot be loaded, and with dynamic coverage
///     mappings that add tests to the static walks' selections.
/// </summary>
public class SelectionIndexTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Token_index_and_full_reference_search_select_the_same_tests(bool throughHelper, bool indexed)
    {
        var repo = throughHelper ? WithNumbersHelper() : FixtureRepo.CreateStandard();
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selector = new TestSelector(engine.Workspace, TimeProvider.System);
        if (indexed)
            await PrepareIndexAsync(engine, selector, corrupt: false);
        var expected = await SelectWithoutIndexAsync(engine);
        var actual = await SelectAsync(engine, selector);

        AssertSelectionsEqual(expected, actual);
        var patterns = Patterns(actual["Lib.Tests"]);
        Assert.Contains("Lib.Tests.CalcTests.Multiplies", patterns);
        Assert.Equal(throughHelper, patterns.Contains("Lib.Tests.CalcTests.MultipliesThroughHelper"));
        Assert.Equal(["Lib.Tests.CalcTests."], Patterns(actual["App.Tests"]));
        Assert.DoesNotContain(patterns, p => p.StartsWith("Lib.Tests.GreeterTests.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_corrupt_index_file_keeps_the_full_search_selection(bool corrupt)
    {
        await using var engine = await InProcessEngine.StartAsync(WithNumbersHelper());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selector = new TestSelector(engine.Workspace, TimeProvider.System);
        await PrepareIndexAsync(engine, selector, corrupt: corrupt);
        var expected = await SelectWithoutIndexAsync(engine);
        var actual = await SelectAsync(engine, selector);

        AssertSelectionsEqual(expected, actual);
    }

    [Fact]
    public async Task Dynamic_map_adds_an_extra_test_to_the_selection()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selector = new TestSelector(engine.Workspace, TimeProvider.System);
        selector.DynamicMap.RecordMappings(
            "Lib.Tests/Lib.Tests.csproj",
            engine.Repo.Root,
            methodToTests: new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["Lib.Calc.Mul"] = ["Lib.Tests.CalcTests.Adds"],
            });
        await PrepareIndexAsync(engine, selector, corrupt: false);
        var expected = await SelectWithoutIndexAsync(engine);
        var actual = await SelectAsync(engine, selector);

        Assert.Equal(["Lib.Tests.CalcTests.Adds", "Lib.Tests.CalcTests.Multiplies"], Patterns(actual["Lib.Tests"]));
        Assert.Equal(["Lib.Tests.CalcTests."], Patterns(actual["App.Tests"]));
        Assert.Equal(Patterns(expected["App.Tests"]), Patterns(actual["App.Tests"]));
    }

    [Fact]
    public async Task Dynamic_map_does_not_remove_the_test_selected_by_the_static_walks()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var selector = new TestSelector(engine.Workspace, TimeProvider.System);
        selector.DynamicMap.RecordMappings(
            "Lib.Tests/Lib.Tests.csproj",
            engine.Repo.Root,
            methodToTests: new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["Lib.Calc.Mul"] = ["Lib.Tests.GreeterTests.Greets"],
            });
        await PrepareIndexAsync(engine, selector, corrupt: false);

        var selection = await SelectAsync(engine, selector);

        Assert.Equal(["Lib.Tests.CalcTests.Multiplies", "Lib.Tests.GreeterTests.Greets"], Patterns(selection["Lib.Tests"]));
        Assert.Equal(["Lib.Tests.CalcTests."], Patterns(selection["App.Tests"]));
    }

    private static async Task<Dictionary<string, TestSelection>> SelectWithoutIndexAsync(InProcessEngine engine)
    {
        await engine.Workspace.SyncAsync([engine.Repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken);
        var graph = engine.Workspace.Graph;
        var lib = graph.Projects.Single(p => p.Name == "Lib");
        var reached = new[] { lib }.Concat(graph.DependentsOf(lib)).DistinctBy(p => p.Path).ToList();
        var generation = await engine.Workspace.EnsureLoadedAndPinAsync(reached, TestContext.Current.CancellationToken);
        var projects = reached.SelectMany(p => RepoWorkspace.ProjectsFor(generation.Current, p)).ToList();
        var walk = new MemberWalk(engine.Repo.Root, generation, generation.Current, projects, TimeProvider.System);
        var seeds = new List<(Document Document, SyntaxNode Node)>();
        seeds.AddRange(await walk.SeedAsync(engine.Repo.PathOf("Lib/Calc.cs"), TestContext.Current.CancellationToken));
        var classLevel = await new TypeWalk(engine.Workspace).SelectAsync(projects, seeds, TestContext.Current.CancellationToken);
        var outcome = await walk.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Completed);
        return walk.Selections.ToDictionary(
            kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute),
            kv => classLevel.GetValueOrDefault(kv.Key) is TestSelection.Whole whole ? whole : kv.Value);
    }

    private static async Task PrepareIndexAsync(InProcessEngine engine, TestSelector selector, bool corrupt)
    {
        var directory = Path.Combine(selector.TokenIndex.Directory);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "current.bin");
        if (corrupt)
        {
            File.WriteAllText(file, "not a token index");
        }
        else
        {
            var path = engine.Repo.PathOf("Lib/Calc.cs");
            await engine.Workspace.SyncAsync([path], TestContext.Current.CancellationToken);
            var graph = engine.Workspace.Graph;
            var lib = graph.Projects.Single(p => p.Name == "Lib");
            var reached = new[] { lib }.Concat(graph.DependentsOf(lib)).DistinctBy(p => p.Path).ToList();
            var generation = await engine.Workspace.EnsureLoadedAndPinAsync(reached, TestContext.Current.CancellationToken);
            var projects = reached.SelectMany(p => RepoWorkspace.ProjectsFor(generation.Current, p)).ToList();
            await selector.TokenIndex.EnsureIndexedAsync(generation.Current, projects, TestContext.Current.CancellationToken);
            selector.TokenIndex.Save("current");
        }

        Assert.True(File.Exists(file));
    }

    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, TestSelector selector)
    {
        var path = engine.Repo.PathOf("Lib/Calc.cs");
        await engine.Workspace.SyncAsync([path], TestContext.Current.CancellationToken);
        var result = await selector.SelectAsync([path], TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    private static string[] Patterns(TestSelection selection)
    {
        Assert.IsType<TestSelection.Methods>(selection);
        return [.. ((TestSelection.Methods)selection).Patterns.Order(StringComparer.Ordinal)];
    }

    private static void AssertSelectionsEqual(
        IReadOnlyDictionary<string, TestSelection> expected,
        IReadOnlyDictionary<string, TestSelection> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (project, selection) in expected)
            Assert.Equal(Patterns(selection), Patterns(actual[project]));
    }

    private static FixtureRepo WithNumbersHelper()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib.Tests/Numbers.cs", """
            using Lib;

            namespace Lib.Tests;

            internal static class Numbers
            {
                public static int Product(int a, int b) => new Calc().Mul(a, b);
            }
            """);
        repo.Replace("Lib.Tests/CalcTests.cs", "    private static Calc NewCalc() => new();", """
                [Fact]
                public void MultipliesThroughHelper() => Assert.Equal(6, Numbers.Product(2, 3));

                private static Calc NewCalc() => new();
            """);
        repo.Commit("add a helper for member selection");
        return repo;
    }
}
