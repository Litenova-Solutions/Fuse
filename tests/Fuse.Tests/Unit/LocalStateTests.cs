using Fuse.Paths;

namespace Fuse.Tests.Unit;

/// <summary>
///     Engine copies and state directories are removed once nothing uses them: a copy no engine holds, and the state of a
///     repository that no longer exists. Each case runs in a folder of its own, not in the user's local state.
/// </summary>
public class LocalStateTests
{
    private static readonly DateTime Later = DateTime.UtcNow.AddDays(3);

    [Fact]
    public void An_engine_copy_no_engine_holds_is_removed_and_the_current_one_is_kept() =>
        InTempDirectory(copies =>
        {
            var current = Copy(copies, "5.2.0-current");
            var unused = Copy(copies, "5.2.0-unused");

            LocalState.RemoveUnusedEngineCopies(copies, current, Later);

            Assert.True(Directory.Exists(current));
            Assert.False(Directory.Exists(unused));
        });

    [Fact]
    public void An_engine_copy_an_engine_holds_is_kept() =>
        InTempDirectory(copies =>
        {
            var current = Copy(copies, "5.2.0-current");
            var held = Copy(copies, "5.1.0-held");

            using (LocalState.HoldEngineCopy(held))
                LocalState.RemoveUnusedEngineCopies(copies, current, Later);

            Assert.True(Directory.Exists(held));
        });

    [Fact]
    public void An_engine_copy_whose_engine_exited_is_removed() =>
        InTempDirectory(copies =>
        {
            var current = Copy(copies, "5.2.0-current");
            var released = Copy(copies, "5.1.0-released");
            // An engine that was killed leaves its lock file behind, and nothing holds it any more.
            File.WriteAllText(Path.Combine(released, ".in-use-12345"), "");

            LocalState.RemoveUnusedEngineCopies(copies, current, Later);

            Assert.False(Directory.Exists(released));
        });

    [Fact]
    public void A_new_engine_copy_is_kept_while_its_engine_may_still_be_starting() =>
        InTempDirectory(copies =>
        {
            var current = Copy(copies, "5.2.0-current");
            var fresh = Copy(copies, "5.2.0-fresh");

            LocalState.RemoveUnusedEngineCopies(copies, current, DateTime.UtcNow);

            Assert.True(Directory.Exists(fresh));
        });

    [Fact]
    public void The_state_of_a_repository_that_no_longer_exists_is_removed_and_the_rest_is_kept() =>
        InTempDirectory(repositories =>
        {
            var existing = State(repositories, "a", root: repositories);
            var missing = State(repositories, "b", root: Path.Combine(repositories, "deleted-repository"));
            var unknown = State(repositories, "c", root: null);

            Assert.Equal(1, LocalState.RemoveStateOfMissingRepositories(repositories, Later));

            Assert.True(Directory.Exists(existing));
            Assert.False(Directory.Exists(missing));
            Assert.True(Directory.Exists(unknown));
        });

    [Fact]
    public void State_written_to_recently_is_kept_even_when_its_repository_is_gone() =>
        InTempDirectory(repositories =>
        {
            var missing = State(repositories, "b", root: Path.Combine(repositories, "deleted-repository"));

            Assert.Equal(0, LocalState.RemoveStateOfMissingRepositories(repositories, DateTime.UtcNow));
            Assert.True(Directory.Exists(missing));
        });

    [Fact]
    public void State_is_cleaned_up_at_most_once_a_day() =>
        InTempDirectory(repositories =>
        {
            Assert.Equal(0, LocalState.RemoveStateOfMissingRepositories(repositories, Later));
            var missing = State(repositories, "b", root: Path.Combine(repositories, "deleted-repository"));

            Assert.Equal(0, LocalState.RemoveStateOfMissingRepositories(repositories, Later.AddHours(1)));
            Assert.True(Directory.Exists(missing));
            Assert.Equal(1, LocalState.RemoveStateOfMissingRepositories(repositories, Later.AddDays(2)));
        });

    [Fact]
    public void The_repository_of_state_an_earlier_build_wrote_is_read_from_the_engine_log() =>
        InTempDirectory(repositories =>
        {
            var state = Directory.CreateDirectory(Path.Combine(repositories, "old")).FullName;
            File.WriteAllLines(Path.Combine(state, "engine.log"),
            [
                @"2026-09-29 12:14:25.935 engine 5.1.0/6d92847b48ad48609790efb94ad108d0 started for C:\Users\me\AppData\Local\Temp\fuse-tests\blåbærgrød (x) (pid 18356)",
                "2026-09-29 12:14:26.100 evaluated 6 projects in 890 ms (0 failed)",
            ]);

            Assert.Equal(@"C:\Users\me\AppData\Local\Temp\fuse-tests\blåbærgrød (x)", LocalState.RootOf(state));
        });

    private static string Copy(string copies, string name)
    {
        var copy = Directory.CreateDirectory(Path.Combine(copies, name)).FullName;
        File.WriteAllText(Path.Combine(copy, "fuse.dll"), "");
        return copy;
    }

    private static string State(string repositories, string name, string? root)
    {
        var state = Directory.CreateDirectory(Path.Combine(repositories, name)).FullName;
        File.WriteAllText(Path.Combine(state, "engine.log"), "");
        if (root is not null)
            File.WriteAllText(Path.Combine(state, "root"), root);
        return state;
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "local-state-" + Guid.NewGuid().ToString("N")[..8]);
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
