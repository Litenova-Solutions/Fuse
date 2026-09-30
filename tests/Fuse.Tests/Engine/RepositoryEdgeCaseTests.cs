using Fuse.Failures;
using Fuse.Harnesses;
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
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/obj/Generated.cs", "namespace Lib; public class Generated { public int V => \"text\"; }\n");
        // Named alone, it is refused, so the check does not read as a pass either.
        var refused = await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync("Lib/obj/Generated.cs"));
        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("Lib/obj/Generated.cs", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_linked_from_outside_the_repository_through_a_link_is_checked_when_a_change_reaches_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        var shared = repo.Root.Path + "-shared";
        var link = repo.Root.Path + "-shared-link";
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Linked.cs"), "namespace App;\n\npublic static class Linked\n{\n    public static int Sum() => new Lib.Calc().Add(1, 2);\n}\n");
        // A junction needs no privilege on Windows, where a symbolic link does; elsewhere a symbolic link is the link.
        if (OperatingSystem.IsWindows())
            FixtureRepo.Run(Path.GetDirectoryName(link)!, "cmd", "/c", "mklink", "/J", link, shared);
        else
            Directory.CreateSymbolicLink(link, shared);
        try
        {
            repo.Replace("App/App.csproj", "</Project>", $"  <ItemGroup><Compile Include=\"{Path.Combine(link, "Linked.cs")}\" /></ItemGroup>\n</Project>");
            await using var engine = await InProcessEngine.StartAsync(repo);
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

            var report = await engine.CheckAsync("Lib/Calc.cs");

            // Roslyn holds the file under the link's spelling, which is the one the check must look it up by.
            Assert.Contains(report.Errors, e => e.Error.Path.EndsWith("-shared-link/Linked.cs", StringComparison.Ordinal) && e.Error.Id == "CS1061");
            Assert.Contains(report.Errors, e => e.Error.Path == "App/Program.cs");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(shared, recursive: true);
        }
    }

    [Fact]
    public async Task A_generated_file_a_check_names_stays_in_the_baseline()
    {
        await using var engine = await InProcessEngine.StartAsync();
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        const string generated = "Lib/obj/Debug/net10.0/Lib.GlobalUsings.g.cs";
        Assert.NotEmpty(engine.Workspace.Baseline.GetDocumentIdsWithFilePath(engine.Repo.Full(generated)));

        // It has no HEAD content, because build output is not committed; applying it would drop it from the baseline. The
        // check itself is refused, because build output is never a target.
        await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync(generated));

        Assert.NotEmpty(engine.Workspace.Baseline.GetDocumentIdsWithFilePath(engine.Repo.Full(generated)));
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
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(2, 2)");
        var error = await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync("App/Program.cs"));
        Assert.Equal(ErrorCode.LoadFailed, error.Code);
        Assert.Contains("Missing.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_commits_every_error_is_introduced()
    {
        using var repo = FixtureRepo.CreateStandard();
        // Deleting the branch HEAD points at leaves an unborn HEAD: the repository has no commit to compare with.
        FixtureRepo.Run(repo.Root.Path, "git", "update-ref", "-d", "HEAD");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Errors, e => e.Error.Id == "CS0103");
    }
}
