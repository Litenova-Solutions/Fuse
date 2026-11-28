using Fuse.Check.Model;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A gate answered to a registered session checks that session's files alone: with a sibling session's broken
///     files past how many attribution decides, the gate is still clean, while a break the session caused in a
///     sibling's project is still reported. A registered writer counts as the session it was registered under, by
///     either spelling of its name.
/// </summary>
public class SessionGateScopeTests
{
    private const string AddMissing = "'Calc' does not contain a definition for 'Add'";

    [Fact]
    public async Task A_gate_for_a_registered_session_is_clean_when_only_a_sibling_broke_its_own_paths()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Sessions.BeginRegistration("w1", ["App/Report.cs"]);
        engine.Sessions.BeginRegistration("w2", ["Lib/**", "App/Use*.cs"]);
        // w2 renames Calc.Add with 41 callers of its own: past how many files attribution decides, so without
        // the gate's narrowing the sibling's errors would be told undecided.
        var callers = Enumerable.Range(0, 41).Select(i => $"App/Use{i}.cs").ToArray();
        foreach (var (caller, i) in callers.Select((c, i) => (c, i)))
            engine.Repo.Write(caller, $"public static class Use{i} {{ public static int Run() => new Lib.Calc().Add(1, 2); }}\n");
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // w1 edits a clean file in its own paths.
        engine.Repo.Replace("App/Report.cs", "public static string Line(int value) => \"value=\" + Lib.Formatter.Format(value);", "public static string Line(int value) => \"value=\" + Lib.Formatter.Format(value); // w1");
        await engine.SyncAsync([.. callers, "Lib/Calc.cs", "App/Report.cs"]);

        var gate = await engine.CheckAllAsSessionAsync("w1");

        Assert.Empty(gate.Errors);
    }

    [Fact]
    public async Task A_gate_for_a_registered_session_reports_a_break_it_caused_in_a_siblings_project()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Sessions.BeginRegistration("w1", ["Lib/**"]);
        engine.Sessions.BeginRegistration("w2", ["App/**"]);
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");

        var gate = await engine.CheckAllAsSessionAsync("w1");

        Assert.Contains("App/Program.cs", Paths(gate, AddMissing));
    }

    [Fact]
    public async Task A_registered_writer_counts_as_the_session_it_was_registered_under()
    {
        await using var engine = await InProcessEngine.StartAsync();

        Assert.True(engine.Sessions.IsOwnEdit("registered:w1", "w1"));
        Assert.True(engine.Sessions.IsOwnEdit("w1", "registered:w1"));
        Assert.False(engine.Sessions.IsOwnEdit("registered:w2", "w1"));

        engine.Sessions.MarkStopped("registered:w1/child");
        Assert.True(engine.Sessions.IsOwnEdit("registered:w1/child", "w1"));
        Assert.False(engine.Sessions.IsOwnEdit("registered:w1/child", "w2"));
    }

    /// <summary>The files, in order, of the reported errors whose message holds <paramref name="message"/>.</summary>
    private static string[] Paths(CheckResult result, string message) =>
        [.. result.Errors.Where(e => e.Error.Message.Contains(message, StringComparison.Ordinal)).Select(e => e.Error.Path).Order(StringComparer.Ordinal)];
}
