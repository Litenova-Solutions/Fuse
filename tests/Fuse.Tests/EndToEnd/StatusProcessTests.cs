using System.Text.Json;
using System.Text.RegularExpressions;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class StatusProcessTests
{
    [Fact]
    public async Task Status_reports_the_engine_and_the_tree()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var check = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, check.ExitCode);
            var cleanTree = TreeOf(check.Stdout);
            Assert.Contains($"; tree {cleanTree} over 0 changed file(s)", check.Stdout, StringComparison.Ordinal);

            var status = await FuseProcess.RunAsync(repo.Path, null, "status");
            Assert.Equal(0, status.ExitCode);
            var lines = status.Stdout.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.StartsWith($"fuse: engine running for {repo.Root.Path} (pid ", lines[0], StringComparison.Ordinal);
            Assert.Contains(" MB; idle exits after 30 min", lines[0], StringComparison.Ordinal);
            var tree = Regex.Match(lines[1], "^fuse: 0 changed file\\(s\\) since HEAD; tree ([0-9a-f]{12}); HEAD ([0-9a-f]{12})$");
            Assert.True(tree.Success, lines[1]);
            Assert.Equal(cleanTree, tree.Groups[1].Value);
            Assert.Equal(
                "fuse: large-tree limits: past 300 changed files a sync reloads projects; past 500 files in reach a check binds whole projects; commit or run fuse stop and let the next check reload when checks feel slow",
                lines[2]);

            var json = await FuseProcess.RunAsync(repo.Path, null, "status", "--json");
            Assert.Equal(0, json.ExitCode);
            using var document = JsonDocument.Parse(json.Stdout);
            var root = document.RootElement;
            Assert.Equal("running", root.GetProperty("engine").GetString());
            Assert.Equal(int.Parse(Regex.Match(lines[0], @"\(pid (\d+)\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), root.GetProperty("pid").GetInt32());
            Assert.True(root.GetProperty("memoryMB").GetInt32() > 0);
            Assert.Equal(0, root.GetProperty("changedFiles").GetInt32());
            Assert.Equal(tree.Groups[2].Value, root.GetProperty("head").GetString()![..12]);
            Assert.Equal(cleanTree, root.GetProperty("tree").GetString());
            Assert.Equal(300, root.GetProperty("reloadAt").GetInt32());
            Assert.Equal(500, root.GetProperty("wholeProjectAt").GetInt32());

            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
            var edited = await FuseProcess.WaitForStatusAsync(repo.Path, stdout => TreeOf(stdout) != cleanTree, "fuse status never reported the edit");
            var editedTree = TreeOf(edited.Stdout);
            Assert.NotEqual(cleanTree, editedTree);
            Assert.Contains("fuse: 1 changed file(s) since HEAD", edited.Stdout, StringComparison.Ordinal);

            var checkAgain = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, checkAgain.ExitCode);
            Assert.Contains($"; tree {editedTree} over 1 changed file(s)", checkAgain.Stdout, StringComparison.Ordinal);
            var checkHeader = File.ReadLines(Path.Combine(repo.Root.StateDirectory, "last-check-results.txt")).First();
            Assert.Contains($"; tree {editedTree} over 1 changed file(s)", checkHeader, StringComparison.Ordinal);

            var build = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(0, build.ExitCode);
            Assert.Contains($"; tree {editedTree} over 1 changed file(s)", build.Stdout, StringComparison.Ordinal);
            var buildHeader = File.ReadLines(Path.Combine(repo.Root.StateDirectory, "last-build-results.txt")).First();
            Assert.Contains($"; tree {editedTree} over 1 changed file(s)", buildHeader, StringComparison.Ordinal);

            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "stop")).ExitCode);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
            var stopped = await FuseProcess.RunAsync(repo.Path, null, "status");
            Assert.Equal(0, stopped.ExitCode);
            Assert.StartsWith($"fuse: no engine runs for {repo.Root.Path}; the next check starts one", stopped.Stdout, StringComparison.Ordinal);
            Assert.Equal(editedTree, TreeOf(stopped.Stdout));

            var bogus = await FuseProcess.RunAsync(repo.Path, null, "status", "--bogus");
            Assert.Equal(2, bogus.ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Status_outside_a_repository_exits_2()
    {
        var result = await FuseProcess.RunAsync(Path.GetTempPath(), null, "status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not inside a git repository", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>The first tree stamp in <paramref name="output"/>.</summary>
    private static string TreeOf(string output) => Regex.Match(output, "tree ([0-9a-f]{12})").Groups[1].Value;
}
