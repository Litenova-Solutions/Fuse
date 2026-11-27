using System.Collections.Concurrent;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Check.Sessions;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Generations;
namespace Fuse.Tests.Engine;

/// <summary>
/// How a check of every change answers when a tracked source is deleted: the second check is answered from what was kept, and recreating the file with different content runs the check again.
/// </summary>
public class DeletedFileKeptCheckTests
{
    [Fact]
    public async Task A_deleted_file_is_answered_from_the_kept_answer_on_the_second_check()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var checker = new Checker(workspace, new SessionEdits(workspace.Root, null));

        repo.Delete("Lib/Formatter.cs");

        var first = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);
        Assert.Contains(first.Errors, e => e.Error.Path == "App/Report.cs" && e.Error.Id == "CS0234");

        var second = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);

        Assert.Equal(first.ErrorCount, second.ErrorCount);
        Assert.Equal(first.Errors.Select(e => e.Error.Message), second.Errors.Select(e => e.Error.Message));
        Assert.Contains(log, l => l.Contains("check: kept answer", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("could not be read from disk", StringComparison.Ordinal));
        Assert.Equal(1, BindingCount(log));
    }

    [Fact]
    public async Task Recreating_a_deleted_file_with_different_content_runs_the_check_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var checker = new Checker(workspace, new SessionEdits(workspace.Root, null));

        repo.Delete("Lib/Formatter.cs");
        var first = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);
        Assert.Contains(first.Errors, e => e.Error.Path == "App/Report.cs" && e.Error.Id == "CS0234");
        var second = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);
        Assert.Equal(1, log.Count(l => l.Contains("check: kept answer", StringComparison.Ordinal)));

        repo.Write("Lib/Formatter.cs", "namespace Lib;\n\npublic static class Formatter\n{\n    public static string Format(int value) => \"recreated\";\n}\n");
        var third = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);

        Assert.Empty(third.Errors);
        Assert.NotEqual(second.ErrorCount, third.ErrorCount);
        Assert.Equal(1, log.Count(l => l.Contains("check: kept answer", StringComparison.Ordinal)));
        Assert.Equal(2, BindingCount(log));
    }

    [Fact]
    public void A_deleted_file_keys_an_answer_and_recreating_it_changes_the_key()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-deleted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "Calc.cs");
            File.WriteAllText(file, "class Calc {}\n");
            var keyed = new[] { ("Calc.cs", file) };

            var before = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);
            Assert.False(string.IsNullOrEmpty(before));

            File.Delete(file);
            var deleted = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);
            Assert.False(string.IsNullOrEmpty(deleted));
            Assert.NotEqual(before, deleted);

            File.WriteAllText(file, "class Calc { public int Value() => 1; }\n");
            var recreated = KeptCheck.KeyFor("all", 0, null, true, "head", 1, 1, "records", keyed);
            Assert.False(string.IsNullOrEmpty(recreated));
            Assert.NotEqual(deleted, recreated);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int BindingCount(ConcurrentQueue<string> log) =>
        log.Count(l => l.Contains("check: binding ", StringComparison.Ordinal));
}
