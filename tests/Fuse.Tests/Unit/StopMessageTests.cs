using Fuse.Check.Model;
using Fuse.Hooks;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     The text a blocked stop carries: the delta line first, one line per project whose declarations changed with
///     the summary part held back, and guidance instead of a claim about whose edits cause the errors when every
///     error sits in a file the session never wrote.
/// </summary>
public class StopMessageTests
{
    private static ReportedError Error(string file, int line) =>
        new(new CompilerError(file, line, 1, "CS1061", "no Add"));

    private static CheckReport Report(ReportedError[] errors, string[] changedIn, int dependents) =>
        new(errors, 3, ["App"], changedIn, dependents, false, errors.Length, errors.Length, 0);

    [Fact]
    public void A_repeat_block_opens_with_the_delta_line()
    {
        ReportedError[] held = [Error("Lib/Calc.cs", 7)];
        var previous = BlockedStops.StopFor(held, null, DateTimeOffset.UtcNow);
        ReportedError[] current = [Error("Lib/Calc.cs", 7), Error("App/Program.cs", 3)];
        var report = Report(current, [], 0);
        var checkText = "App/Program.cs(3,1): error CS1061: no Add\nfuse: 2 error(s) introduced in 2 file(s) (App)";
        HashSet<string> edited = ["App/Program.cs", "Lib/Calc.cs"];

        var text = StopMessage.Build(checkText, report, previous, edited, "claude:a");

        Assert.Equal("1 new since your last stop, 1 in files you edited", text.Split('\n')[0]);
        Assert.EndsWith(HookCommand.StopReason, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_declarations_print_one_line_per_project_and_leave_the_summary()
    {
        ReportedError[] errors = [Error("App/Program.cs", 3)];
        var report = Report(errors, ["Lib", "Core"], 2);
        var checkText = "App/Program.cs(3,1): error CS1061: no Add\nfuse: 1 error(s) introduced in 1 file(s) (App); Lib, Core declarations changed, 2 dependent project(s) checked";
        HashSet<string> edited = ["App/Program.cs"];

        var text = StopMessage.Build(checkText, report, null, edited, "claude:a");
        var lines = text.Split('\n');

        Assert.Equal("Lib declarations changed", lines[^3]);
        Assert.Equal("Core declarations changed", lines[^2]);
        Assert.Equal(HookCommand.StopReason, lines[^1]);
        Assert.DoesNotContain("Lib, Core declarations changed", text, StringComparison.Ordinal);
        Assert.Contains("fuse: 1 error(s) introduced in 1 file(s) (App)", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_only_in_files_the_session_never_wrote_end_with_guidance()
    {
        ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
        var report = Report(errors, [], 0);
        var checkText = "Lib/Calc.cs(7,1): error CS1061: no Add\nfuse: 1 error(s) introduced in 1 file(s) (Lib)";

        var text = StopMessage.Build(checkText, report, null, new HashSet<string>(StringComparer.Ordinal), "claude:a");

        Assert.EndsWith(StopMessage.GuidanceLine, text, StringComparison.Ordinal);
        Assert.Contains("fuse session begin <name> --paths <glob>", text, StringComparison.Ordinal);
        Assert.Contains("FUSE_SESSION=<name>", text, StringComparison.Ordinal);
        Assert.DoesNotContain(HookCommand.StopReason, text, StringComparison.Ordinal);
        Assert.True(StopMessage.OnlyInFilesNeverWritten(errors, new HashSet<string>(StringComparer.Ordinal)));
        Assert.False(StopMessage.OnlyInFilesNeverWritten(errors, new HashSet<string>(["Lib/Calc.cs"], StringComparer.Ordinal)));
    }

    [Fact]
    public void An_error_the_sessions_own_edit_caused_in_a_file_it_never_wrote_keeps_the_fix_line()
    {
        ReportedError[] errors =
        [
            new ReportedError(new CompilerError("App/Program.cs", 3, 1, "CS1061", "no Add"), new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")) { CausedBy = "claude:a" },
        ];
        var report = Report(errors, [], 0);
        var checkText = "App/Program.cs(3,1): error CS1061: no Add\nfuse: 1 error(s) introduced in 1 file(s) (App)";

        var text = StopMessage.Build(checkText, report, null, new HashSet<string>(StringComparer.Ordinal), "claude:a");

        Assert.EndsWith(HookCommand.StopReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain(StopMessage.GuidanceLine, text, StringComparison.Ordinal);
        Assert.True(StopMessage.CausedBySession(errors, "claude:a"));
        Assert.False(StopMessage.CausedBySession(errors, "claude:b"));
    }

    [Fact]
    public void A_stop_with_no_session_keeps_its_reason_and_adds_guidance()
    {
        ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
        var report = Report(errors, [], 0);
        var checkText = "Lib/Calc.cs(7,1): error CS1061: no Add\nfuse: 1 error(s) introduced in 1 file(s) (Lib)";

        var text = StopMessage.Build(checkText, report, null, new HashSet<string>(StringComparer.Ordinal), null);
        var lines = text.Split('\n');

        Assert.Equal(HookCommand.StopReasonWithoutSession, lines[^2]);
        Assert.Equal(StopMessage.GuidanceLine, lines[^1]);
    }

    [Fact]
    public void An_error_in_a_file_the_session_wrote_keeps_the_fix_line()
    {
        ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
        var report = Report(errors, [], 0);
        var checkText = "Lib/Calc.cs(7,1): error CS1061: no Add\nfuse: 1 error(s) introduced in 1 file(s) (Lib)";
        HashSet<string> edited = ["Lib/Calc.cs"];

        var text = StopMessage.Build(checkText, report, null, edited, "claude:a");

        Assert.EndsWith(HookCommand.StopReason, text, StringComparison.Ordinal);
        Assert.DoesNotContain(StopMessage.GuidanceLine, text, StringComparison.Ordinal);
    }
}
