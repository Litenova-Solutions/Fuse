using Fuse.Operations;
using Fuse.Operations.Test;
using Fuse.Protocol.Requests;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Unit;

/// <summary>How <c>fuse test --all</c> takes the same scope as a plain run, and how a release gate runs every test in its scope.</summary>
public class TestScopeAllTests
{
    private static string Root => FixtureRepo.CheckoutRoot.Path;

    private static string Full(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    [Fact]
    public void An_every_test_plan_defaults_to_the_whole_repository()
    {
        var request = new EngineRequest.PlanAllTests();

        Assert.Empty(request.Projects);
        Assert.False(request.BuildOnly);
    }

    [Fact]
    public void A_project_solution_or_directory_limits_an_every_test_plan()
    {
        Assert.Equal([Full("Lib.Tests/Lib.Tests.csproj")], Scope(["--project", "Lib.Tests/Lib.Tests.csproj"])!.Paths);
        Assert.Equal([Full("Shop.slnx")], Scope(["--solution", "Shop.slnx"])!.Paths);
        Assert.Equal([Full("tests/Foo.Tests")], Scope(["tests/Foo.Tests"])!.Paths);
    }

    [Fact]
    public void Build_switches_alone_scope_an_every_test_plan_of_the_repository()
    {
        var scope = Scope(["-c", "Release", "--no-build"])!;

        Assert.Empty(scope.Paths);
        Assert.Equal(["-c", "Release", "--no-build"], scope.BuildSwitches);
        Assert.True(scope.BuildOnly);
        Assert.Equal("Release", scope.Configuration);
        Assert.False(TestOperation.IsReleaseGate(scope));
    }

    [Fact]
    public void A_release_gate_names_a_configuration_other_than_debug_with_no_build()
    {
        Assert.True(TestOperation.IsReleaseGate(Scope(["App.slnx", "-c", "Release", "--no-build"])!));
        Assert.True(TestOperation.IsReleaseGate(Scope(["--project", "T.csproj", "--configuration=Release", "--no-build"])!));
        Assert.False(TestOperation.IsReleaseGate(Scope(["App.slnx", "-c", "Debug", "--no-build"])!));
        Assert.False(TestOperation.IsReleaseGate(Scope(["App.slnx", "-c", "Release"])!));
        Assert.False(TestOperation.IsReleaseGate(Scope(["-c", "Release", "--no-build"])!));
        Assert.False(TestOperation.IsReleaseGate(Scope(["App.slnx", "-f", "net10.0", "--no-build"])!));
    }

    [Fact]
    public void A_rewritten_release_gate_scopes_the_every_test_run()
    {
        // A hook rewrites dotnet test App.slnx -c Release --no-build to fuse test with the same arguments.
        var scope = Scope(["App.slnx", "-c", "Release", "--no-build"])!;

        Assert.Equal([Full("App.slnx")], scope.Paths);
        Assert.True(TestOperation.IsReleaseGate(scope));
    }

    [Fact]
    public void Choosing_tests_is_no_scope_for_an_every_test_run()
    {
        Assert.Null(Scope(["--filter", "Name=A"]));
        Assert.Null(Scope(["--project", "T.csproj", "--filter", "Name=A"]));
    }

    [Fact]
    public void No_affected_test_in_a_project_names_the_every_test_run_that_covers_it()
    {
        var scope = Scope(["--project", "Lib.Tests/Lib.Tests.csproj"])!;

        Assert.Equal(
            $"fuse: 0 tests affected in Lib.Tests; run 'fuse test --all --project {Full("Lib.Tests/Lib.Tests.csproj")}' to run every test in it",
            TestOperation.ScopedEmptyMessage(scope));
    }

    private static TestScopeArguments? Scope(string[] arguments) =>
        Assert.IsType<ParseResult<TestScopeArguments?>.Parsed>(TestScopeArguments.Parse(Root, arguments)).Value;
}
