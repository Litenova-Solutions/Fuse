using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>How change tracking treats <c>.fuseignore</c>: an edit to the file is a project input, and a project file it lists is ignored.</summary>
public class FuseIgnoreTrackingTests
{
    [Fact]
    public void The_ignore_file_is_a_project_input()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { [".fuseignore"] = "samples/**\n" });

        Assert.True(PathRules.IsProjectFile(repo.Root.PathOf(repo.Full(".fuseignore")).Absolute));
        Assert.True(FuseIgnore.IsFile(repo.Root.PathOf(repo.Full(".fuseignore"))));
        Assert.False(FuseIgnore.IsFile(repo.Root.PathOf(repo.Full("samples/.fuseignore"))));
    }

    [Fact]
    public void A_listed_project_file_is_ignored_and_others_are_not()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { [".fuseignore"] = "# templates\n\nsamples/**\n" });
        var ignore = FuseIgnore.Read(repo.Root);

        Assert.True(ignore.Ignores(repo.Root.PathOf(repo.Full("samples/Demo/Demo.csproj"))));
        Assert.False(ignore.Ignores(repo.Root.PathOf(repo.Full("src/App/App.csproj"))));
    }
}
