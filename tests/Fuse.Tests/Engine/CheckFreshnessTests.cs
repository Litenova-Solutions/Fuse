using System.Collections.Concurrent;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check of every change never keeps an answer a changed file could not be read for: the answer is still bound
///     from its snapshot, the engine log says it is not kept, and the next check binds again instead of reusing it.
/// </summary>
public class CheckFreshnessTests
{
    [Fact]
    public async Task A_check_of_every_change_does_not_keep_an_answer_a_changed_file_could_not_be_read_for()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        var calc = repo.Full("Lib/Calc.cs");
        var broken = "";
        var restoring = false;
        // The file is readable again before the check ends, with the same content it holds in the next check, so a kept
        // first answer would serve the next check without binding.
        using var workspace = new RepoWorkspace(repo.Root, message =>
        {
            log.Enqueue(message);
            if (restoring && message.StartsWith("check: binding ", StringComparison.Ordinal))
            {
                restoring = false;
                Directory.Delete(calc);
                File.WriteAllText(calc, broken);
            }
        });
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var checker = new Checker(workspace, new SessionEdits(workspace.Root, null));

        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        broken = repo.Read("Lib/Calc.cs");
        await workspace.SyncAsync([repo.PathOf("Lib/Calc.cs")], cancellationToken);
        File.Delete(calc);
        Directory.CreateDirectory(calc);

        restoring = true;
        var first = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);
        restoring = false;

        Assert.NotEmpty(first.Errors);
        Assert.Contains(log, l => l.Contains("changed file(s) could not be read from disk, so this answer is not kept", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.StartsWith("check coverage:", StringComparison.Ordinal));
        Assert.Equal(1, BindingCount(log));

        var second = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);

        Assert.Equal(2, BindingCount(log));
        var error = Assert.Single(second.Errors).Error;
        Assert.Equal("Lib/Calc.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
    }

    /// <summary>How many checks bound anything: a check answered from what was kept binds nothing more.</summary>
    private static int BindingCount(ConcurrentQueue<string> log) =>
        log.Count(l => l.Contains("check: binding ", StringComparison.Ordinal));
}
