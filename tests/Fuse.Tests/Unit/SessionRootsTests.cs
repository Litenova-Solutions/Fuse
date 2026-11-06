using Fuse.Hooks;

namespace Fuse.Tests.Unit;

/// <summary>
///     The roots a session's post-edit hooks touched: each root is held once however often it is recorded, one
///     session's roots never meet another's, and a session that touched nothing holds none.
/// </summary>
public class SessionRootsTests
{
    [Fact]
    public void A_root_is_held_once_however_often_it_is_recorded() =>
        InTempDirectory(directory =>
        {
            SessionRoots.Record(directory, "claude:a", "/repos/one");
            SessionRoots.Record(directory, "claude:a", "/repos/one");
            SessionRoots.Record(directory, "claude:a", "/repos/two");

            Assert.Equal(["/repos/one", "/repos/two"], SessionRoots.Read(directory, "claude:a"));
        });

    [Fact]
    public void One_sessions_roots_never_meet_anothers() =>
        InTempDirectory(directory =>
        {
            SessionRoots.Record(directory, "claude:a", "/repos/one");
            SessionRoots.Record(directory, "claude:b", "/repos/two");

            Assert.Equal(["/repos/one"], SessionRoots.Read(directory, "claude:a"));
            Assert.Equal(["/repos/two"], SessionRoots.Read(directory, "claude:b"));
            Assert.Empty(SessionRoots.Read(directory, "claude:c"));
        });

    [Fact]
    public void The_file_for_a_session_names_no_other_session() =>
        InTempDirectory(directory =>
        {
            var first = SessionRoots.FileFor(directory, "claude:a");
            var second = SessionRoots.FileFor(directory, "claude:b");

            Assert.NotEqual(first, second);
            Assert.True(first.StartsWith(Path.Combine(directory, "session-roots"), StringComparison.Ordinal));
        });

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-session-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
