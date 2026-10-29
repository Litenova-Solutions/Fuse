using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A stop check answered to a session checks only the files that session wrote, jointly wrote, or no session is
///     credited with, and what they reach. The told errors are the same as attribution decides on a full check, and a
///     stop with no session still binds every change.
/// </summary>
public class StopScopeTests
{
    private const string AddMissing = "'Calc' does not contain a definition for 'Add'";
    private const string GreetMissing = "'IGreeter' does not contain a definition for 'Greet'";

    [Fact]
    public async Task A_stop_scope_binds_only_the_stopping_sessions_files_and_reach()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);

        var scoped = await engine.CheckAllAsSessionAsync("A");
        var full = await engine.CheckAllAsync();

        // The told set is attribution's answer: the Add errors, and the one Greet error in reach left out.
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(scoped, AddMissing));
        Assert.Empty(Paths(scoped, GreetMissing));
        Assert.Equal(1, scoped.LeftToOtherSessions);
        // The other session's target-only files were never bound.
        Assert.DoesNotContain(scoped.Errors, e => e.Error is { Path: "Lib/Greeting.cs", Id: "CS0535" });
        Assert.DoesNotContain(scoped.Errors, e => e.Error.Path == "Lib.Tests/GreeterTests.cs");
        Assert.True(scoped.FilesChecked < full.FilesChecked);
    }

    [Fact]
    public async Task A_stop_with_no_session_still_binds_every_change()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);

        var all = await engine.CheckAllAsync();

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(all, AddMissing));
        Assert.Equal(["App/Program.cs"], Paths(all, GreetMissing));
        Assert.Contains(all.Errors, e => e.Error is { Path: "Lib/Greeting.cs", Id: "CS0535" });
        Assert.Equal(0, all.LeftToOtherSessions);
    }

    [Fact]
    public async Task A_stop_scope_tells_an_error_that_needs_both_sessions_edits()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/Helper.cs", "namespace Lib;\n\npublic static class Helper\n{\n    public static int Twice(int x) => 2 * x;\n}\n");
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(Lib.Helper.Twice(value, 1))");
        await engine.SyncAsync("Lib/Helper.cs", "App/Report.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Helper.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("App/Report.cs")]);

        var a = await engine.CheckAllAsSessionAsync("A");
        var b = await engine.CheckAllAsSessionAsync("B");

        Assert.Contains(a.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS1501" });
        Assert.Contains(b.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS1501" });
    }

    [Fact]
    public async Task A_stop_scope_tells_an_error_in_a_file_both_sessions_wrote()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingFactor;");
        await engine.SyncAsync("Lib/Calc.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var a = await engine.CheckAllAsSessionAsync("A");

        Assert.Contains(a.Errors, e => e.Error is { Path: "Lib/Calc.cs", Id: "CS0103" });
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(a, AddMissing));
    }

    [Fact]
    public async Task A_stop_scope_tells_an_error_no_session_is_credited_with()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2));", "calc.Add(1, 2));\nSystem.Console.WriteLine(missingName);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs", "App/Program.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);

        var a = await engine.CheckAllAsSessionAsync("A");

        Assert.Contains(a.Errors, e => e.Error is { Path: "App/Program.cs", Id: "CS0103" });
        Assert.Empty(Paths(a, GreetMissing));
    }

    [Fact]
    public async Task A_stopped_subagents_files_stay_in_the_parents_stop_scope()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Greeting.cs", "Lib/Calc.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("A/worker", [engine.Repo.PathOf("Lib/Greeting.cs")]);

        var running = await engine.CheckAllAsSessionAsync("A");
        Assert.Empty(Paths(running, GreetMissing));

        engine.Sessions.MarkStopped("A/worker");
        var stopped = await engine.CheckAllAsSessionAsync("A");
        Assert.Equal(["App/Program.cs", "Lib.Tests/GreeterTests.cs"], Paths(stopped, GreetMissing));
        Assert.Equal(0, stopped.LeftToOtherSessions);
    }

    private static string[] Paths(Fuse.Check.Model.CheckResult result, string message) =>
        [.. result.Errors.Where(e => e.Error.Message.Contains(message, StringComparison.Ordinal)).Select(e => e.Error.Path).Order(StringComparer.Ordinal)];
}
