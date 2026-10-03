using Fuse.Check.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Two sessions, A and B, writing one working tree, each with a breaking change. A check answered to a session tells
///     it the errors its own edits cause, alone or together with the other session's; the errors the other session's
///     edits cause without it are left out and counted. A check answered to no session, and the stop hook's check of every
///     change, report every error as before.
/// </summary>
public class SessionAttributionTests
{
    private const string AddMissing = "'Calc' does not contain a definition for 'Add'";
    private const string GreetMissing = "'IGreeter' does not contain a definition for 'Greet'";

    [Fact]
    public async Task An_error_only_one_sessions_change_causes_is_told_to_that_session_alone()
    {
        await using var engine = await InProcessEngine.StartAsync();
        BreakAddAndGreet(engine);

        var a = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");
        var b = await engine.CheckAsSessionAsync("B", "Lib/Greeting.cs");

        // App/Program.cs calls both Add and Greet, so both checks reach it, and both errors in it are found by both.
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(a, AddMissing));
        Assert.Empty(Paths(a, GreetMissing));
        Assert.Equal(1, a.LeftToOtherSessions);

        Assert.Equal(["App/Program.cs", "Lib.Tests/GreeterTests.cs"], Paths(b, GreetMissing));
        Assert.Empty(Paths(b, AddMissing));
        Assert.Equal(1, b.LeftToOtherSessions);
        // B's own file, where Greeter no longer implements the interface, is B's alone.
        Assert.Contains(b.Errors, e => e.Error is { Path: "Lib/Greeting.cs", Id: "CS0535" });
    }

    [Fact]
    public async Task A_check_answered_to_no_session_reports_every_error()
    {
        await using var engine = await InProcessEngine.StartAsync();
        BreakAddAndGreet(engine);

        var result = await engine.CheckAsync("Lib/Calc.cs");

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(result, AddMissing));
        Assert.Equal(["App/Program.cs"], Paths(result, GreetMissing));
        Assert.Equal(0, result.LeftToOtherSessions);
    }

    [Fact]
    public async Task One_session_alone_is_told_every_error_without_binding_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        // An error nobody is credited with; with no other session, there is nothing to leave out.
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2));", "calc.Add(1, 2));\nSystem.Console.WriteLine(missingName);");

        var (result, phases) = await CheckAsSessionWithPhasesAsync(engine, "A", "Lib/Calc.cs");

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(result, AddMissing));
        Assert.Contains(result.Errors, e => e.Error.Id == "CS0103");
        Assert.Equal(0, result.LeftToOtherSessions);
        Assert.DoesNotContain(phases, p => p.Phase == Fuse.Telemetry.Phase.Attribution);
    }

    [Fact]
    public async Task An_error_that_needs_both_sessions_edits_is_told_to_both()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // A adds a type; B calls it with an argument too many. Without A's file the call names a type that does not exist,
        // a different error; without B's file there is no call.
        engine.Repo.Write("Lib/Helper.cs", "namespace Lib;\n\npublic static class Helper\n{\n    public static int Twice(int x) => 2 * x;\n}\n");
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(Lib.Helper.Twice(value, 1))");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Helper.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("App/Report.cs")]);

        var a = await engine.CheckAsSessionAsync("A", "Lib/Helper.cs");
        var b = await engine.CheckAsSessionAsync("B", "App/Report.cs");

        Assert.Contains(a.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS1501" });
        Assert.Contains(b.Errors, e => e.Error is { Path: "App/Report.cs", Id: "CS1501" });
        Assert.Equal(0, a.LeftToOtherSessions);
        Assert.Equal(0, b.LeftToOtherSessions);
    }

    [Fact]
    public async Task An_error_in_a_file_both_sessions_wrote_is_told_to_both()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // Both write Lib/Calc.cs: A renames Add, B breaks Mul's body. Each also has a file of its own, so attribution runs.
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * missingFactor;");
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \"", "\"Hi \"");
        engine.Repo.Replace("Lib/Formatter.cs", "value.ToString(", "(value + 0).ToString(");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs"), engine.Repo.PathOf("Lib/Greeting.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs"), engine.Repo.PathOf("Lib/Formatter.cs")]);

        var a = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");
        var b = await engine.CheckAsSessionAsync("B", "Lib/Calc.cs");

        foreach (var result in new[] { a, b })
        {
            Assert.Contains(result.Errors, e => e.Error is { Path: "Lib/Calc.cs", Id: "CS0103" });
            Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(result, AddMissing));
            Assert.Equal(0, result.LeftToOtherSessions);
        }
    }

    [Fact]
    public async Task An_error_from_an_edit_no_session_is_credited_with_is_told_to_every_session()
    {
        await using var engine = await InProcessEngine.StartAsync();
        BreakAddAndGreet(engine);
        // Written without a post-edit hook, as a shell command or a generator would.
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2));", "calc.Add(1, 2));\nSystem.Console.WriteLine(missingName);");

        var a = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");
        var b = await engine.CheckAsSessionAsync("B", "Lib/Greeting.cs");

        Assert.Contains(a.Errors, e => e.Error is { Path: "App/Program.cs", Id: "CS0103" });
        Assert.Contains(b.Errors, e => e.Error is { Path: "App/Program.cs", Id: "CS0103" });
        // The other session's errors are still left out.
        Assert.Empty(Paths(a, GreetMissing));
        Assert.Empty(Paths(b, AddMissing));
    }

    [Fact]
    public async Task The_check_of_every_change_reports_every_sessions_errors()
    {
        await using var engine = await InProcessEngine.StartAsync();
        BreakAddAndGreet(engine);
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2));", "calc.Add(1, 2));\nSystem.Console.WriteLine(missingName);");
        await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");
        await engine.CheckAsSessionAsync("B", "Lib/Greeting.cs");

        var all = await engine.CheckAllAsync();

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(all, AddMissing));
        Assert.Equal(["App/Program.cs", "Lib.Tests/GreeterTests.cs"], Paths(all, GreetMissing));
        Assert.Contains(all.Errors, e => e.Error is { Path: "App/Program.cs", Id: "CS0103" });
        Assert.Contains(all.Errors, e => e.Error is { Path: "Lib/Greeting.cs", Id: "CS0535" });
        Assert.Equal(0, all.LeftToOtherSessions);
    }

    [Fact]
    public async Task A_file_that_matches_HEAD_again_is_no_longer_credited()
    {
        await using var engine = await InProcessEngine.StartAsync();
        BreakAddAndGreet(engine);
        await engine.CheckAsSessionAsync("B", "Lib/Greeting.cs");
        // B undoes its change, and writes it again without a post-edit hook.
        var greeting = engine.Repo.Read("Lib/Greeting.cs");
        engine.Repo.Replace("Lib/Greeting.cs", "string Hail(string name);", "string Greet(string name);");
        await engine.CheckAllAsync();
        engine.Repo.Write("Lib/Greeting.cs", greeting);

        var a = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");

        // Nobody is credited with Lib/Greeting.cs now, so its break is everyone's.
        Assert.Equal(["App/Program.cs"], Paths(a, GreetMissing));
        Assert.Equal(0, a.LeftToOtherSessions);
    }

    /// <summary>A renames <c>Calc.Add</c>; B renames the interface's <c>Greet</c>. <c>App/Program.cs</c> calls both.</summary>
    private static void BreakAddAndGreet(InProcessEngine engine)
    {
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);
    }

    private static async Task<(CheckResult Result, IReadOnlyList<(string Phase, double Ms)> Phases)> CheckAsSessionWithPhasesAsync(InProcessEngine engine, string session, params string[] files)
    {
        var scope = engine.Files(files);
        engine.Sessions.Record(session, scope.Paths);
        var phases = new Fuse.Telemetry.PhaseTimes();
        var result = await engine.Checker.CheckAsync(scope, session, phases, TestContext.Current.CancellationToken);
        return (result, phases.All);
    }

    /// <summary>The files, in order, of the reported errors whose message holds <paramref name="message"/>.</summary>
    private static string[] Paths(CheckResult result, string message) =>
        [.. result.Errors.Where(e => e.Error.Message.Contains(message, StringComparison.Ordinal)).Select(e => e.Error.Path).Order(StringComparer.Ordinal)];
}
