using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     Which <c>dotnet test</c> arguments only name where the tests are and how to build them, so the affected tests
///     are planned in that scope, and which choose the tests themselves and pass through (#69).
/// </summary>
public class TestScopeArgumentsTests
{
    private static string Root => FixtureRepo.CheckoutRoot.Path;

    private static string Full(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    [Fact]
    public void A_project_directory_is_a_scope()
    {
        var scope = TestScopeArguments.Parse(Root, ["tests/Foo.Tests"]);

        Assert.NotNull(scope);
        Assert.Equal([Full("tests/Foo.Tests")], scope.Paths);
        Assert.Empty(scope.BuildSwitches);
        Assert.False(scope.BuildOnly);
    }

    [Fact]
    public void Project_and_solution_options_are_a_scope_in_both_spellings()
    {
        var scope = TestScopeArguments.Parse(Root, ["--project", "Foo.Tests/Foo.Tests.csproj", "--solution=Bar.slnx"]);

        Assert.NotNull(scope);
        Assert.Equal([Full("Foo.Tests/Foo.Tests.csproj"), Full("Bar.slnx")], scope.Paths);
    }

    [Fact]
    public void Build_switches_alone_are_a_scope_of_the_repository()
    {
        var scope = TestScopeArguments.Parse(Root, ["--no-build"]);

        Assert.NotNull(scope);
        Assert.Empty(scope.Paths);
        Assert.Equal(["--no-build"], scope.BuildSwitches);
        Assert.False(scope.BuildOnly);
    }

    [Fact]
    public void A_configuration_or_framework_makes_every_run_build()
    {
        var scope = TestScopeArguments.Parse(Root, ["Fuse.slnx", "-c", "Release", "--framework=net10.0"]);

        Assert.NotNull(scope);
        Assert.Equal([Full("Fuse.slnx")], scope.Paths);
        Assert.Equal(["-c", "Release", "-f", "net10.0"], scope.BuildSwitches);
        Assert.True(scope.BuildOnly);
        Assert.Equal("Release", scope.Configuration);
        Assert.Equal("net10.0", scope.Framework);
    }

    [Fact]
    public void Restore_logo_terminal_and_verbosity_switches_are_scope_arguments()
    {
        var scope = TestScopeArguments.Parse(Root, ["--no-restore", "--nologo", "-tl:off", "-v", "q", "--verbosity=minimal"]);

        Assert.NotNull(scope);
        Assert.Empty(scope.Paths);
        Assert.Empty(scope.BuildSwitches);
    }

    [Theory]
    [InlineData("--filter", "Name=A")]
    [InlineData("--filter-method", "*Adds")]
    [InlineData("--filter-class", "Ns.CalcTests")]
    [InlineData("--settings", "x.runsettings")]
    [InlineData("--logger", "trx")]
    [InlineData("--collect", "XPlat Code Coverage")]
    [InlineData("--", "--filter-method", "X")]
    [InlineData("--results-directory", "out")]
    [InlineData("--blame")]
    [InlineData("-p:ContinuousIntegrationBuild=true")]
    public void Arguments_that_choose_or_shape_the_run_pass_through(params string[] arguments) =>
        Assert.Null(TestScopeArguments.Parse(Root, arguments));

    [Theory]
    [InlineData("tests/Foo.Tests", "--filter", "Name=A")]
    [InlineData("--no-build", "--treenode-filter", "/*/*/*/Adds")]
    [InlineData("--project")]
    public void One_non_scope_argument_makes_the_whole_list_pass_through(params string[] arguments) =>
        Assert.Null(TestScopeArguments.Parse(Root, arguments));
}
