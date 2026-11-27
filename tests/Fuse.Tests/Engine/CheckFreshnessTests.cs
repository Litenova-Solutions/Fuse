using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Check.Sessions;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Generations;
namespace Fuse.Tests.Engine;

/// <summary>
///     A check of every change never keeps an answer a changed file could not be read for: the file holds the
///     broken content in the view but cannot be read from disk, the engine log says the answer is not kept,
///     and the next check binds again instead of reusing it.
/// </summary>
public class CheckFreshnessTests
{
    [Fact]
    public async Task A_check_of_every_change_does_not_keep_an_answer_a_changed_file_could_not_be_read_for()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var checker = new Checker(workspace, new SessionEdits(workspace.Root, null));

        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        await workspace.SyncAsync([repo.PathOf("Lib/Calc.cs")], cancellationToken);

        // The file is unreadable without changing its content, so the check binds its errors from the view
        // yet keeps no answer under a key that leaves the file out. A file held open without sharing denies
        // reads only on Windows, so this uses permissions, which deny reads everywhere.
        var restore = MakeUnreadable(repo.Full("Lib/Calc.cs"));
        try
        {
            var first = await checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, cancellationToken);

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
        finally
        {
            restore();
        }
    }

    /// <summary>Makes the file at <paramref name="path"/> unreadable to the current user and returns how to make it readable again.</summary>
    private static Action MakeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
            return DenyReadOnWindows(path);

        return DenyReadOnUnix(path);
    }

    /// <summary>Removes every permission from <paramref name="path"/> and returns how to put its mode back.</summary>
    [UnsupportedOSPlatform("windows")]
    private static Action DenyReadOnUnix(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return () => File.SetUnixFileMode(path, mode);
    }

    /// <summary>Denies the current user read access to <paramref name="path"/> and returns how to put its list back.</summary>
    [SupportedOSPlatform("windows")]
    private static Action DenyReadOnWindows(string path)
    {
        var info = new FileInfo(path);
        var original = info.GetAccessControl();
        var updated = info.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().Name ?? Environment.UserName;
        updated.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read, AccessControlType.Deny));
        info.SetAccessControl(updated);
        return () => info.SetAccessControl(original);
    }

    /// <summary>How many checks bound anything: a check answered from what was kept binds nothing more.</summary>
    private static int BindingCount(ConcurrentQueue<string> log) =>
        log.Count(l => l.Contains("check: binding ", StringComparison.Ordinal));
}
