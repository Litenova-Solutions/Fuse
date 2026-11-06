using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;

namespace Fuse.Tests.Engine;

/// <summary>
///     The state a request writes before its check runs is held once: a session root and a written file are
///     recorded once for a session, so a post-edit for a file and a repository already credited writes no state
///     file, and the result store holds a report for at most as many sessions as it holds answers.
/// </summary>
public class RequestOverheadTests
{
    private static readonly DateTimeOffset BlockedAt = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_root_already_recorded_writes_no_state_file() =>
        InTempDirectory(directory =>
        {
            SessionRoots.Record(directory, "claude:a", "/repos/one");
            var file = SessionRoots.FileFor(directory, "claude:a");
            var written = File.GetLastWriteTimeUtc(file);
            var held = File.ReadAllBytes(file);

            SessionRoots.Record(directory, "claude:a", "/repos/one");

            Assert.Equal(held, File.ReadAllBytes(file));
            Assert.Equal(written, File.GetLastWriteTimeUtc(file));
            Assert.Equal(["/repos/one"], SessionRoots.Read(directory, "claude:a"));
        });

    [Fact]
    public void A_file_already_written_since_the_last_block_writes_no_state_file() =>
        InTempDirectory(directory =>
        {
            StopMemory.NoteWrittenOnce(directory, "claude:a", ["Lib/Calc.cs"], DateTimeOffset.MinValue, BlockedAt.AddMinutes(-5));
            var file = Assert.Single(Directory.EnumerateFiles(directory, "stop-written-*.tsv"));
            var held = File.ReadAllBytes(file);

            StopMemory.NoteWrittenOnce(directory, "claude:a", ["Lib/Calc.cs"], DateTimeOffset.MinValue, BlockedAt.AddMinutes(-1));

            Assert.Equal(held, File.ReadAllBytes(file));
            Assert.Equal(["Lib/Calc.cs"], StopMemory.AllWritten(directory, "claude:a").Order(StringComparer.Ordinal));
        });

    [Fact]
    public void A_file_written_after_a_block_is_recorded_once_more() =>
        InTempDirectory(directory =>
        {
            ReportedError[] errors = [new ReportedError(new CompilerError("Lib/Calc.cs", 7, 1, "CS1061", "no Add"))];
            StopMemory.Save(directory, "claude:a", StopMemory.SnapshotFor(errors, "9f2c41aa07b3", BlockedAt));
            StopMemory.NoteWrittenOnce(directory, "claude:a", ["Lib/Calc.cs"], DateTimeOffset.MinValue, BlockedAt.AddMinutes(-5));
            Assert.Empty(StopMemory.WrittenSince(directory, "claude:a", BlockedAt));

            StopMemory.NoteWrittenOnce(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt, BlockedAt.AddMinutes(1));
            var file = Assert.Single(Directory.EnumerateFiles(directory, "stop-written-*.tsv"));
            var held = File.ReadAllBytes(file);
            StopMemory.NoteWrittenOnce(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt, BlockedAt.AddMinutes(2));

            Assert.Equal(held, File.ReadAllBytes(file));
            Assert.Equal(["Lib/Calc.cs"], StopMemory.WrittenSince(directory, "claude:a", BlockedAt).Order(StringComparer.Ordinal));
        });

    [Fact]
    public void A_session_past_the_kept_answers_holds_its_answer_in_its_session_file_alone()
    {
        var state = NewStateDirectory();
        try
        {
            var store = new ResultStore(state);
            for (var i = 0; i < 33; i++)
                store.Keep($"key-{i}", $"worker-{i}", generation: 1, "0123456789ab", Report("CS0103"));

            Assert.False(store.TryGetSession("worker-0", out _));
            Assert.True(store.TryGetSession("worker-32", out _));
            Assert.True(File.Exists(store.SessionFilePath("worker-0")));
        }
        finally
        {
            DeleteDirectory(state);
        }
    }

    private static CheckReport Report(string id) => new(
        [new ReportedError(new CompilerError("Lib/Calc.cs", 10, 5, id, "the name does not exist"))],
        1,
        ["Lib"],
        [],
        0,
        false,
        1,
        1,
        0);

    private static string NewStateDirectory()
    {
        var state = Path.Combine(Path.GetTempPath(), "fuse-request-overhead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        return state;
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = NewStateDirectory();
        try
        {
            test(directory);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A state directory a test could not delete is in the temp directory, which the system cleans.
        }
    }
}
