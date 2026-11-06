using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class LockRefreshTests
{
    private const string ContentHash = "App/App.csproj : error NU1403: Package content hash validation failed for Contoso 1.0.0.";

    [Fact]
    public void A_content_hash_failure_names_the_refresh_command_and_the_lock_files()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["Fuse.slnx"] = "<Solution />\n",
            ["App/packages.lock.json"] = "{}\n",
            ["Lib/packages.lock.json"] = "{}\n",
        });

        var answer = BuildOperation.Render(new ProcessResult(1, ContentHash), repo.Root.Path, 3, "build", BuildOptions.Default);

        Assert.Equal(
            [
                "App/App.csproj: error NU1403: Package content hash validation failed for Contoso 1.0.0.",
                "  lock file: run dotnet restore Fuse.slnx --force-evaluate to refresh App/packages.lock.json, Lib/packages.lock.json",
                "fuse: build failed with 1 error(s) in 3.0 s",
            ],
            answer.Text.Split('\n'));
    }

    [Fact]
    public void The_arguments_name_what_to_restore_when_they_name_a_project_or_a_solution()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["A.slnx"] = "<Solution />\n",
            ["B.sln"] = "<Solution />\n",
            ["App/packages.lock.json"] = "{}\n",
        });

        var answer = BuildOperation.Render(new ProcessResult(1, ContentHash), repo.Root.Path, 3, "build", BuildOptions.Default, restoreTarget: "App/App.csproj");

        Assert.Contains("lock file: run dotnet restore App/App.csproj --force-evaluate to refresh App/packages.lock.json", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_lock_files_the_line_says_what_the_restore_refreshes()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Fuse.slnx"] = "<Solution />\n" });

        Assert.Equal(
            "lock file: run dotnet restore Fuse.slnx --force-evaluate to refresh the restore",
            BuildOperation.LockRefreshLine(repo.Root.Path, "Fuse.slnx"));
    }

    [Fact]
    public void Without_one_solution_the_command_names_no_target()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["A.slnx"] = "<Solution />\n",
            ["B.sln"] = "<Solution />\n",
            ["App/packages.lock.json"] = "{}\n",
        });

        Assert.Equal(
            "lock file: run dotnet restore --force-evaluate to refresh App/packages.lock.json",
            BuildOperation.LockRefreshLine(repo.Root.Path, null));
    }

    [Fact]
    public void The_line_prints_once_for_many_failures_and_never_in_json()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Fuse.slnx"] = "<Solution />\n" });
        var output = ContentHash + "\n" + ContentHash.Replace("App/App.csproj", "Lib/Lib.csproj", StringComparison.Ordinal);

        var text = BuildOperation.Render(new ProcessResult(1, output), repo.Root.Path, 3, "build", BuildOptions.Default);
        var json = BuildOperation.Render(new ProcessResult(1, output), repo.Root.Path, 3, "build", new BuildOptions(int.MaxValue, true, false));

        Assert.Equal(1, text.Text.Split('\n').Count(l => l.Contains("lock file:", StringComparison.Ordinal)));
        Assert.DoesNotContain("lock file:", json.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_errors_get_no_line()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Fuse.slnx"] = "<Solution />\n" });

        var answer = BuildOperation.Render(new ProcessResult(1, "Lib/Calc.cs(1,1): error CS1002: ; expected"), repo.Root.Path, 3, "build", BuildOptions.Default);

        Assert.DoesNotContain("lock file:", answer.Text, StringComparison.Ordinal);
        Assert.True(new BuildDiagnostic("App/App.csproj", null, null, "NU1403", "hash").IsContentHashFailure);
        Assert.False(new BuildDiagnostic("Lib/Calc.cs", 1, 1, "CS1002", "; expected").IsContentHashFailure);
    }

    [Fact]
    public void Lock_files_in_build_output_are_not_listed()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Fuse.slnx"] = "<Solution />\n" });
        repo.Write("App/obj/packages.lock.json", "{}\n");

        Assert.Equal(
            "lock file: run dotnet restore Fuse.slnx --force-evaluate to refresh the restore",
            BuildOperation.LockRefreshLine(repo.Root.Path, "Fuse.slnx"));
    }

    [Fact]
    public void Past_ten_lock_files_the_rest_are_only_counted()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Fuse.slnx"] = "<Solution />\n" });
        foreach (var i in Enumerable.Range(1, 11))
            repo.Write($"P{i}/packages.lock.json", "{}\n");

        var line = BuildOperation.LockRefreshLine(repo.Root.Path, "Fuse.slnx");

        Assert.StartsWith("lock file: run dotnet restore Fuse.slnx --force-evaluate to refresh P1/packages.lock.json", line, StringComparison.Ordinal);
        Assert.EndsWith("and 1 more", line, StringComparison.Ordinal);
    }
}
