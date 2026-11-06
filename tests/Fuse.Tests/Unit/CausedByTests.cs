using System.Text.Json;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The session that caused an error, named in <c>--json</c> as <c>causedBy</c>: the one session that wrote the
///     file holding the cause's declaration, so the answer can be routed to the session that can fix it. A cause in
///     a file several sessions wrote, or no session is credited with, names none, and the text answer is unchanged.
/// </summary>
public class CausedByTests
{
    // The paths here name files but read none, so any root will do.
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;

    private static readonly Cause Cause = new Cause.Removed("public int Add(int a, int b)");

    private static readonly CompilerError Error = new("App/Program.cs", 4, 40, "CS1061", "'Calc' does not contain a definition for 'Add'");

    [Fact]
    public void An_error_whose_cause_one_session_wrote_names_that_session()
    {
        var attached = Attach(new Dictionary<RepoPath, IReadOnlySet<string>> { [Root.PathOf("Lib/Calc.cs")] = Sessions("worker-c") });

        Assert.Equal("worker-c", Assert.Single(attached).CausedBy);
    }

    [Fact]
    public void A_registered_session_is_named_as_it_was_registered()
    {
        var attached = Attach(new Dictionary<RepoPath, IReadOnlySet<string>> { [Root.PathOf("Lib/Calc.cs")] = Sessions("registered:worker-7") });

        Assert.Equal("worker-7", Assert.Single(attached).CausedBy);
    }

    [Fact]
    public void A_cause_several_sessions_wrote_or_none_wrote_names_no_session()
    {
        Assert.Null(Assert.Single(Attach(new Dictionary<RepoPath, IReadOnlySet<string>> { [Root.PathOf("Lib/Calc.cs")] = Sessions("worker-a", "worker-c") })).CausedBy);
        Assert.Null(Assert.Single(Attach(new Dictionary<RepoPath, IReadOnlySet<string>>())).CausedBy);
        Assert.Null(Assert.Single(CauseLines.Attach([Error], Reach(), Root, [])).CausedBy);
    }

    [Fact]
    public void The_causing_session_reaches_the_json_line_and_the_text_answer_names_none()
    {
        var attached = Attach(new Dictionary<RepoPath, IReadOnlySet<string>> { [Root.PathOf("Lib/Calc.cs")] = Sessions("worker-c") });
        var result = new CheckResult(attached, FilesChecked: 2, Projects: ["App"], DeclarationsChangedIn: ["Lib"], DependentProjectsChecked: 1, CheckedWholeProjects: false, ErrorCount: 1, ErrorFileCount: 1, LeftToOtherSessions: 0);
        var report = ResponseMapper.Report(result);
        var reported = Assert.Single(report.Errors);

        Assert.Equal("worker-c", reported.CausedBy);
        var line = DiagnosticJson.CheckLine(reported);
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("worker-c", parsed.RootElement.GetProperty("causedBy").GetString());
        Assert.Equal("removed: public int Add(int a, int b)", parsed.RootElement.GetProperty("cause").GetString());

        // The text answer prints the error and its cause as before; the session is a JSON field only.
        var text = CheckOperation.Render(report).Text;
        Assert.Contains("removed: public int Add(int a, int b)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-c", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_error_without_a_causing_session_omits_the_field()
    {
        var line = DiagnosticJson.CheckLine(new ReportedError(Error));

        using var parsed = JsonDocument.Parse(line);
        Assert.False(parsed.RootElement.TryGetProperty("causedBy", out _));
    }

    private static IReadOnlyList<IntroducedError> Attach(IReadOnlyDictionary<RepoPath, IReadOnlySet<string>> writers) =>
        CauseLines.Attach([Error], Reach(), Root, [], writers);

    private static Reach.Precise Reach() => new(
        new Dictionary<RepoPath, Cause> { [Root.PathOf(Error.Path)] = Cause },
        CauseFiles: new Dictionary<Cause, RepoPath> { [Cause] = Root.PathOf("Lib/Calc.cs") });

    private static HashSet<string> Sessions(params string[] sessions) => new HashSet<string>(sessions, StringComparer.Ordinal);
}
