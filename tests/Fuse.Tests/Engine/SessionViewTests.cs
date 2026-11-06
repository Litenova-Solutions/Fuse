using Fuse.Check.Model;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

/// <summary>
///     A session view is kept by the writer: the tree view with the files only other live sessions wrote at HEAD.
///     A sync applies its files to the kept view, the kept views are bounded with the one used least recently going
///     first, and a session whose view went derives it again and is answered the same. A session that has stopped
///     is settled, so its edits stay in every view.
/// </summary>
public class SessionViewTests
{
    private const string AddMissing = "'Calc' does not contain a definition for 'Add'";
    private const string GreetMissing = "'IGreeter' does not contain a definition for 'Greet'";

    [Fact]
    public async Task A_session_view_is_kept_across_a_sync_and_updated_with_the_tree()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.CheckAsync("Lib/Calc.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new[] { engine.Repo.PathOf("Lib/Calc.cs") };

        var first = await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken);

        // A later sync patches the file the view holds at HEAD; the writer leaves that file at HEAD in the
        // kept view, so the view is the same one, neither derived again nor holding the tree's new text.
        engine.Repo.Replace("Lib/Calc.cs", "public int Plus(", "public int PlusPlus(");
        await engine.SyncAsync("Lib/Calc.cs");
        Assert.Same(first, await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken));

        // A later sync patches another file; the writer applies it to the kept view instead of deriving it
        // again. A solution is immutable, so applying the file forks the view: it is a new instance, and its
        // identity is asserted above, on a sync whose file the view holds at HEAD.
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        await engine.SyncAsync("App/Report.cs");
        var second = await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken);

        // The kept view holds the patched file as the tree has it, and the file at HEAD as HEAD has it.
        Assert.Contains("value: ", await TextOfAsync(second, "App/Report.cs", cancellationToken), StringComparison.Ordinal);
        var calc = await TextOfAsync(second, "Lib/Calc.cs", cancellationToken);
        Assert.Contains("public int Add(", calc, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Plus", calc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_kept_views_are_bounded_with_the_one_used_least_recently_going_first()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("Lib/Calc.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        string[] files = ["Lib/Calc.cs", "Lib/Greeting.cs", "Lib/Formatter.cs", "App/Program.cs", "App/Report.cs", "Lib.Tests/CalcTests.cs", "App.Tests/ReportTests.cs"];
        // Each listed file is changed in the tree and synced, so a view that puts it back at HEAD is a
        // solution of its own: with the file as HEAD leaves it, each view would be the tree view itself
        // and neither the kept view nor the one derived again after it goes could be told from it.
        foreach (var file in files)
            engine.Repo.Write(file, engine.Repo.Read(file) + "\n// changed in the tree\n");
        await engine.SyncAsync(files);
        var sets = files.Select(f => new[] { engine.Repo.PathOf(f) })
            .Concat([new[] { engine.Repo.PathOf(files[0]), engine.Repo.PathOf(files[1]) }, new[] { engine.Repo.PathOf(files[0]), engine.Repo.PathOf(files[2]) }])
            .ToList();
        Assert.Equal(SolutionViews.MaxSessionViews + 1, sets.Count);

        var views = new List<Solution>();
        foreach (var set in sets)
            views.Add(await engine.Workspace.CurrentWithHeadContentAsync(set, cancellationToken));

        // The ninth view put the first out; the ninth is still kept, and the first derives again when asked.
        Assert.Same(views[^1], await engine.Workspace.CurrentWithHeadContentAsync(sets[^1], cancellationToken));
        Assert.NotSame(views[0], await engine.Workspace.CurrentWithHeadContentAsync(sets[0], cancellationToken));
    }

    [Fact]
    public async Task A_session_whose_view_was_shed_is_answered_the_same_from_a_view_derived_again()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await BreakAddAndGreetAsync(engine);
        await engine.Workspace.ClearSessionViewsAsync(TestContext.Current.CancellationToken);

        var a = await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");

        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], Paths(a, AddMissing));
        Assert.Empty(Paths(a, GreetMissing));
        Assert.Equal(1, a.LeftToOtherSessions);
    }

    [Fact]
    public async Task A_stopped_top_level_session_keeps_its_errors_from_another_session()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await BreakAddAndGreetAsync(engine);
        var before = await engine.CheckAllAsSessionAsync("A");
        engine.Sessions.MarkStopped("B");

        var after = await engine.CheckAllAsSessionAsync("A");

        // A harness fires a top-level session's stop at the end of every turn while it keeps working, so B's stop
        // changes nothing for A: what only B's edits cause stays B's.
        Assert.Equal(before.Errors.Select(e => (e.Error.Path, e.Error.Id)).Order(), after.Errors.Select(e => (e.Error.Path, e.Error.Id)).Order());
        Assert.Equal(before.LeftToOtherSessions, after.LeftToOtherSessions);
    }

    [Fact]
    public async Task A_check_names_the_session_whose_change_caused_an_error_in_another_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SyncAsync("Lib/Calc.cs");
        engine.Sessions.Record("C", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var result = await engine.CheckAsync("Lib/Calc.cs");

        var caused = result.Errors.Where(e => e.Error.Path == "App/Program.cs").ToList();
        Assert.NotEmpty(caused);
        Assert.All(caused, e => Assert.Equal("C", e.CausedBy));
    }

    /// <summary>
    ///     A renames <c>Calc.Add</c>; B renames the interface's <c>Greet</c>. <c>App/Program.cs</c> calls both. Both
    ///     files are synced, as each session's own post-edit hook would have synced them.
    /// </summary>
    private static async Task BreakAddAndGreetAsync(InProcessEngine engine)
    {
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Greeting.cs")]);
    }

    /// <summary>The text of <paramref name="relative"/> as <paramref name="solution"/> holds it.</summary>
    private static async Task<string> TextOfAsync(Solution solution, string relative, CancellationToken cancellationToken)
    {
        var document = solution.Projects.SelectMany(p => p.Documents)
            .First(d => d.FilePath is { } file && file.Replace('\\', '/').EndsWith("/" + relative, StringComparison.Ordinal));
        return (await document.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>The files, in order, of the reported errors whose message holds <paramref name="message"/>.</summary>
    private static string[] Paths(CheckResult result, string message) =>
        [.. result.Errors.Where(e => e.Error.Message.Contains(message, StringComparison.Ordinal)).Select(e => e.Error.Path).Order(StringComparer.Ordinal)];
}
