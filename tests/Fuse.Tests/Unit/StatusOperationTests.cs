using System.Text.Json;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class StatusOperationTests
{
    [Fact]
    public async Task Without_an_engine_status_reads_the_tree_from_git()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var result = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal($"fuse: no engine runs for {repo.Root.Path}; the next check starts one", lines[0]);
        Assert.Matches("^fuse: 0 changed file\\(s\\) since HEAD; tree [0-9a-f]{12}; HEAD [0-9a-f]{12}$", lines[1]);
        Assert.Equal("fuse: large-tree limits: past 300 changed C# sources in one sync a sync reloads projects; past 500 changed files a check skips its kept answer; past 500 files in reach a check binds whole projects; past 2000 touched paths projects re-evaluate", lines[2]);
    }

    [Fact]
    public async Task An_edit_changes_the_count_and_the_tree()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var before = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        repo.Replace("Lib/Calc.cs", "{}", "{ }");
        var after = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        Assert.Matches("^fuse: 1 changed file\\(s\\) since HEAD; tree [0-9a-f]{12}; HEAD [0-9a-f]{12}$", after.Text.Split('\n')[1]);
        Assert.NotEqual(before.Text.Split('\n')[1], after.Text.Split('\n')[1]);
    }

    [Fact]
    public async Task Json_names_the_engine_the_tree_and_the_limits()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        repo.Replace("Lib/Calc.cs", "{}", "{ }");

        var result = await StatusOperation.RunAsync(repo.Root, true, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Text);
        var root = document.RootElement;
        Assert.Equal("stopped", root.GetProperty("engine").GetString());
        Assert.False(root.TryGetProperty("pid", out _));
        Assert.False(root.TryGetProperty("memoryMB", out _));
        Assert.Equal(1, root.GetProperty("changedFiles").GetInt32());
        Assert.Equal(40, root.GetProperty("head").GetString()!.Length);
        Assert.Equal(12, root.GetProperty("tree").GetString()!.Length);
        Assert.False(root.TryGetProperty("changedSourcesInReach", out _));
        Assert.False(root.TryGetProperty("touchedPaths", out _));
        Assert.Equal(2000, root.GetProperty("touchedLimit").GetInt32());
        Assert.Equal(500, root.GetProperty("keptAnswerAt").GetInt32());
        Assert.Equal(500, root.GetProperty("wholeProjectAt").GetInt32());
        Assert.Equal(300, root.GetProperty("syncReloadAt").GetInt32());
        Assert.False(root.TryGetProperty("reloadMs", out _));
        Assert.False(root.TryGetProperty("gateHolder", out _));
    }

    [Fact]
    public async Task Past_three_hundred_changed_files_status_names_the_limits_without_a_commit_warning()
    {
        using var repo = FixtureRepo.CreateEmpty(Enumerable.Range(1, 301).ToDictionary(i => $"F{i}/C{i}.cs", i => $"public class C{i} {{}}\n"));
        foreach (var i in Enumerable.Range(1, 301))
            repo.Replace($"F{i}/C{i}.cs", "{}", "{ }");

        var result = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        var lines = result.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("fuse: 301 changed file(s) since HEAD; tree ", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("warning", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("commit soon", result.Text, StringComparison.Ordinal);
        Assert.Equal("fuse: large-tree limits: past 300 changed C# sources in one sync a sync reloads projects; past 500 changed files a check skips its kept answer; past 500 files in reach a check binds whole projects; past 2000 touched paths projects re-evaluate", lines[2]);
    }

    [Fact]
    public async Task Many_non_source_changes_do_not_count_toward_the_source_count()
    {
        var files = Enumerable.Range(1, 20).ToDictionary(i => $"Notes/N{i}.md", i => $"notes {i}\n");
        files["Lib/Calc.cs"] = "public class Calc {}\n";
        using var repo = FixtureRepo.CreateEmpty(files);
        foreach (var i in Enumerable.Range(1, 20))
            repo.Write($"Notes/N{i}.md", $"changed {i}\n");
        repo.Replace("Lib/Calc.cs", "{}", "{ }");

        var result = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        Assert.Matches("^fuse: 1 changed file\\(s\\) since HEAD; tree [0-9a-f]{12}; HEAD [0-9a-f]{12}$", result.Text.Split('\n')[1]);
        Assert.DoesNotContain("warning", result.Text, StringComparison.Ordinal);
    }
}
