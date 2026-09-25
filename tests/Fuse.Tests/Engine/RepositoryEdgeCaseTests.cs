using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

public class RepositoryEdgeCaseTests
{
    [Fact]
    public async Task A_repository_without_projects_is_detected_without_an_engine()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["index.js"] = "export const a = 1;\n", ["Stray.cs"] = "class Stray {}\n" });
        Assert.False(await RepoProbe.HasCSharpProjectsAsync(repo.Root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_repository_with_a_project_is_detected()
    {
        using var repo = FixtureRepo.CreateStandard();
        Assert.True(await RepoProbe.HasCSharpProjectsAsync(repo.Root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Init_refuses_a_repository_without_projects()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["index.js"] = "export const a = 1;\n" });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, InitCommand.Run(repo.Root.Path, output, error));
        Assert.Contains("no C# projects", error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.Full(".claude")));
    }

    [Fact]
    public async Task A_file_in_build_output_is_never_checked()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Write("Lib/obj/Generated.cs", "namespace Lib; public class Generated { public int V => \"text\"; }\n");
        var report = await engine.CheckAsync("Lib/obj/Generated.cs");
        Assert.Empty(report.Introduced);
        Assert.Equal(0, report.FilesChecked);
    }

    [Fact]
    public async Task A_missing_head_is_a_clear_failure()
    {
        using var repo = FixtureRepo.CreateStandard();
        File.Delete(repo.Full(".git/HEAD"));
        using var workspace = new RepoWorkspace(repo.Root, _ => { });
        var error = await Assert.ThrowsAsync<FuseException>(() => workspace.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.LoadFailed, error.Code);
        Assert.Contains("git failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_project_reference_is_reported_not_checked()
    {
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("App/App.csproj", "</Project>", "  <ItemGroup><ProjectReference Include=\"..\\Missing\\Missing.csproj\" /></ItemGroup>\n</Project>");
        await using var engine = await EngineHarness.StartAsync(repo);
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(2, 2)");
        var error = await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync("App/Program.cs"));
        Assert.Equal(ErrorCode.LoadFailed, error.Code);
        Assert.Contains("Missing.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_commits_every_error_is_new()
    {
        using var repo = FixtureRepo.CreateStandard();
        // Deleting the branch HEAD points at leaves an unborn HEAD: the repository has no commit to compare with.
        FixtureRepo.Run(repo.Root.Path, "git", "update-ref", "-d", "HEAD");
        await using var engine = await EngineHarness.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Introduced, d => d.Id == "CS0103");
    }
}
