using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     How patterns combine while a walk selects tests. A class pattern ends with a dot and covers every test name that
///     starts with it, so a test name next to its own class adds nothing to the filter.
/// </summary>
public class SelectionBuilderTests
{
    [Fact]
    public void A_test_name_its_class_already_covers_is_not_added()
    {
        var builder = new SelectionBuilder();
        var project = TestProject();
        Assert.True(builder.Add(project, "Lib.Tests.CalcTests."));
        Assert.True(builder.Add(project, "Lib.Tests.CalcTests.Adds"));
        Assert.Equal(["Lib.Tests.CalcTests."], Patterns(builder, project));
    }

    [Fact]
    public void A_class_pattern_replaces_the_test_names_it_covers()
    {
        var builder = new SelectionBuilder();
        var project = TestProject();
        builder.Add(project, "Lib.Tests.CalcTests.Adds");
        builder.Add(project, "Lib.Tests.GreeterTests.Greets");
        Assert.True(builder.Add(project, "Lib.Tests.CalcTests."));
        Assert.Equal(["Lib.Tests.CalcTests.", "Lib.Tests.GreeterTests.Greets"], Patterns(builder, project));
    }

    [Fact]
    public void A_class_pattern_does_not_cover_a_class_whose_name_only_starts_the_same()
    {
        var builder = new SelectionBuilder();
        var project = TestProject();
        builder.Add(project, "Lib.Tests.Calc.");
        builder.Add(project, "Lib.Tests.CalcTests.Adds");
        Assert.Equal(["Lib.Tests.Calc.", "Lib.Tests.CalcTests.Adds"], Patterns(builder, project));
    }

    private static string[] Patterns(SelectionBuilder builder, ProjectNode project) =>
        [.. Assert.IsType<TestSelection.Methods>(builder.Selections[project.Path]).Patterns.Order(StringComparer.Ordinal)];

    /// <summary>A test project; its paths are never read, so any root will do.</summary>
    private static ProjectNode TestProject() => new()
    {
        Path = FixtureRepo.CheckoutRoot.PathOf("Lib.Tests/Lib.Tests.csproj"),
        Name = "Lib.Tests",
        Directory = FixtureRepo.CheckoutRoot.PathOf("Lib.Tests"),
        References = [],
        Sources = new HashSet<RepoPath>(),
        EvaluationInputs = new HashSet<RepoPath>(),
        AssetsFile = FixtureRepo.CheckoutRoot.PathOf("Lib.Tests/obj/project.assets.json"),
        IsTest = true,
        IsExecutable = false,
        UsesTestingPlatform = false,
    };
}
