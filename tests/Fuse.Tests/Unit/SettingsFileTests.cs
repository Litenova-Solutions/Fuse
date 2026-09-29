using Fuse.Harnesses;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     Replacing a settings file retries while Windows reports the file as in use. The retry cases inject the move and
///     the wait, so they run without holding a file open and without waiting.
/// </summary>
public class SettingsFileTests
{
    [Fact]
    public void A_move_onto_a_file_in_use_is_tried_again_until_it_succeeds()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        SettingsFile.MoveWithRetry(
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new UnauthorizedAccessException("Access to the path is denied.");
                if (attempts == 2)
                    throw new IOException("The process cannot access the file because it is being used by another process.");
            },
            waits.Add);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)], waits);
    }

    [Fact]
    public void A_move_that_keeps_failing_throws_the_last_failure_after_five_attempts()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var thrown = Assert.Throws<UnauthorizedAccessException>(() => SettingsFile.MoveWithRetry(() => throw new UnauthorizedAccessException($"attempt {++attempts}"), waits.Add));

        Assert.Equal("attempt 5", thrown.Message);
        Assert.Equal(5, attempts);
        Assert.Equal(4, waits.Count);
    }

    [Fact]
    public void Any_other_failure_is_thrown_at_once()
    {
        var attempts = 0;
        Assert.Throws<ArgumentException>(() => SettingsFile.MoveWithRetry(
            () =>
            {
                attempts++;
                throw new ArgumentException("bad path");
            },
            _ => Assert.Fail("a failure that is not the file being in use is not retried")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Writing_replaces_the_file_and_leaves_no_temporary_file()
    {
        var directory = FixtureRepo.NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            var root = RepoRoot.Find(directory)!;
            var path = Path.Combine(root.Path, ".claude", "settings.json");
            SettingsFile.WriteText(root, path, "first");
            Assert.Equal(".claude/settings.json", SettingsFile.WriteText(root, path, "second"));

            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
