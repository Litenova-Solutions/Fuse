using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary><c>fuse check</c> names the changed files it does not check, capped, and the post-edit hook leaves them out.</summary>
public class NonCSharpRenderTests
{
    [Fact]
    public void A_clean_check_names_the_non_csharp_files_it_did_not_check()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0) { NonCSharpChanged = 2, NonCSharpSample = ["apps/web/src/App.tsx", "apps/web/src/api.ts"] });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no errors introduced (3 file(s) checked; 2 non-C# file(s) changed, which Fuse does not check: apps/web/src/App.tsx, apps/web/src/api.ts)", result.Text);
    }

    [Fact]
    public void The_sample_caps_at_five_and_counts_the_rest()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0) { NonCSharpChanged = 7, NonCSharpSample = ["a.ts", "b.ts", "c.ts", "d.ts", "e.ts", "f.ts"] });

        Assert.Equal("fuse: no errors introduced (3 file(s) checked; 7 non-C# file(s) changed, which Fuse does not check: a.ts, b.ts, c.ts, d.ts, e.ts, ... and 2 more)", result.Text);
    }

    [Fact]
    public void Errors_and_the_note_share_the_summary()
    {
        ReportedError[] errors = [new(new CompilerError("Lib/Calc.cs", 1, 1, "CS0103", "no x"))];

        var result = CheckOperation.Render(new CheckReport(errors, 1, ["Lib"], [], 0, false, 1, 1, 0) { NonCSharpChanged = 1, NonCSharpSample = ["notes/app.tsx"] });

        Assert.Equal(1, result.ExitCode);
        Assert.EndsWith("; 1 non-C# file(s) changed, which Fuse does not check: notes/app.tsx", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_non_csharp_changes_the_answer_reads_as_before()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], ["Lib"], 2, false, 0, 0, 0));

        Assert.Equal("fuse: no errors introduced (3 file(s) checked; Lib declarations changed, 2 dependent project(s) checked)", result.Text);
    }

    [Fact]
    public void The_post_edit_hook_leaves_the_note_out()
    {
        var report = new CheckReport([], 3, [], [], 0, false, 0, 0, 0) { NonCSharpChanged = 2, NonCSharpSample = ["a.ts", "b.ts"] };

        Assert.Equal("fuse: no errors introduced (3 file(s) checked)", CheckOperation.Render(report, includeNonCSharp: false).Text);
    }
}
