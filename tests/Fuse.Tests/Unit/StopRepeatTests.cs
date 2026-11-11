using Fuse.Check.Model;
using Fuse.Hooks;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     The blocked stops a session carries between stops: the blocked answer it last met reads back from its file, the
///     same answer counts as a repeat only while no error sits in a file written after that block, and the delta
///     counts what the blocked answer did not hold.
/// </summary>
public class StopRepeatTests
{
    private static ReportedError Error(string file, int line, string message = "no Add") =>
        new(new CompilerError(file, line, 1, "CS1061", message));

    private static readonly DateTimeOffset BlockedAt = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_blocked_answer_reads_back_from_its_file() =>
        InTempDirectory(directory =>
        {
            ReportedError[] errors = [Error("Lib/Calc.cs", 7), Error("App/Program.cs", 3)];
            BlockedStops.Save(directory, "claude:a", BlockedStops.StopFor(errors, "9f2c41aa07b3", BlockedAt));

            var loaded = BlockedStops.Load(directory, "claude:a");

            Assert.NotNull(loaded);
            Assert.Equal("9f2c41aa07b3", loaded.Tree);
            Assert.Equal(BlockedAt, loaded.BlockedAt);
            Assert.Equal(errors.Select(BlockedStops.KeyOf).Order(StringComparer.Ordinal), loaded.Errors.Order(StringComparer.Ordinal));
            Assert.Null(BlockedStops.Load(directory, "claude:other"));
        });

    [Fact]
    public void The_same_errors_with_no_write_after_the_block_count_as_a_repeat() =>
        InTempDirectory(directory =>
        {
            ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
            var previous = BlockedStops.StopFor(errors, "9f2c41aa07b3", BlockedAt);
            BlockedStops.NoteWritten(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt.AddMinutes(-1));

            var writtenSince = BlockedStops.WrittenSince(directory, "claude:a", BlockedAt);

            Assert.Empty(writtenSince);
            Assert.True(BlockedStops.IsRepeat(previous, errors, "9f2c41aa07b3", writtenSince));
        });

    [Fact]
    public void An_error_in_a_file_written_after_the_block_blocks_again() =>
        InTempDirectory(directory =>
        {
            ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
            var previous = BlockedStops.StopFor(errors, "9f2c41aa07b3", BlockedAt);
            BlockedStops.NoteWritten(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt.AddMinutes(1));

            var writtenSince = BlockedStops.WrittenSince(directory, "claude:a", BlockedAt);

            Assert.Equal(["Lib/Calc.cs"], writtenSince.Order(StringComparer.Ordinal));
            Assert.False(BlockedStops.IsRepeat(previous, errors, "9f2c41aa07b3", writtenSince));
        });

    [Fact]
    public void A_different_error_set_or_tree_blocks_again()
    {
        ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
        var previous = BlockedStops.StopFor(errors, "9f2c41aa07b3", BlockedAt);
        var none = new HashSet<string>(StringComparer.Ordinal);

        Assert.False(BlockedStops.IsRepeat(previous, [Error("Lib/Calc.cs", 8)], "9f2c41aa07b3", none));
        Assert.False(BlockedStops.IsRepeat(previous, errors, "aaaaaaaaaaaa", none));
        Assert.False(BlockedStops.IsRepeat(null, errors, "9f2c41aa07b3", none));
        Assert.True(BlockedStops.IsRepeat(previous, errors, null, none));
    }

    [Fact]
    public void The_delta_counts_errors_the_blocked_answer_did_not_hold()
    {
        ReportedError[] held = [Error("Lib/Calc.cs", 7)];
        var previous = BlockedStops.StopFor(held, null, BlockedAt);
        ReportedError[] current = [Error("Lib/Calc.cs", 7), Error("App/Program.cs", 3), Error("App/Report.cs", 4, "wrong argument")];
        HashSet<string> edited = ["App/Program.cs"];

        Assert.Equal((2, 1), BlockedStops.Delta(previous, current, edited));
        Assert.Equal((3, 1), BlockedStops.Delta(null, current, edited));
    }

    [Fact]
    public void A_file_written_twice_is_written_after_the_block_only_by_its_later_write() =>
        InTempDirectory(directory =>
        {
            BlockedStops.NoteWritten(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt.AddMinutes(-5));
            Assert.Empty(BlockedStops.WrittenSince(directory, "claude:a", BlockedAt));
            Assert.Equal(["Lib/Calc.cs"], BlockedStops.AllWritten(directory, "claude:a").Order(StringComparer.Ordinal));

            BlockedStops.NoteWritten(directory, "claude:a", ["Lib/Calc.cs"], BlockedAt.AddMinutes(5));
            Assert.Equal(["Lib/Calc.cs"], BlockedStops.WrittenSince(directory, "claude:a", BlockedAt).Order(StringComparer.Ordinal));
        });

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-stop-memory-" + Guid.NewGuid().ToString("N"));
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
