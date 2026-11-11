using Fuse.Check;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The declaration filter in front of a session view: an error whose bound symbol traces to a declaration
///     exactly one session changed is decided from that trace, and everything the trace cannot decide is declined
///     to the view, which answers it as a check without the filter does.
/// </summary>
public class DeclarationFilterTests
{
    [Fact]
    public async Task A_member_made_inaccessible_by_one_session_is_decided_from_its_trace()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "private int Add(");
        var result = await engine.CheckAsync("Lib/Calc.cs");
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var error = Assert.Single(result.Errors, e => e.Error.Path == "App/Program.cs").Error;
        Assert.Equal("CS0122", error.Id);
        var generation = engine.Workspace.PinGeneration();
        var writers = engine.Sessions.WritersOf(generation.Changed);
        var filter = new DeclarationFilter(engine.Workspace, engine.Sessions);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The caller file is credited to no session, so the trace holds only B's changed declaration: B is told,
        // and a live session that did not make the change is not, without either binding a view.
        Assert.Equal(DeclarationFilter.Decision.Told, await filter.DecideAsync(error, "B", generation, writers, cancellationToken));
        Assert.Equal(DeclarationFilter.Decision.NotTold, await filter.DecideAsync(error, "A", generation, writers, cancellationToken));
    }

    [Fact]
    public async Task A_removed_member_is_declined_to_the_view()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var result = await engine.CheckAsync("Lib/Calc.cs");
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var error = Assert.Single(result.Errors, e => e.Error.Path == "App/Program.cs").Error;
        Assert.Equal("CS1061", error.Id);
        var generation = engine.Workspace.PinGeneration();
        var filter = new DeclarationFilter(engine.Workspace, engine.Sessions);

        // A rename is a removal of the old declaration and an addition of the new one, which the view decides.
        Assert.Equal(
            DeclarationFilter.Decision.Declined,
            await filter.DecideAsync(error, "A", generation, engine.Sessions.WritersOf(generation.Changed), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_declaration_two_sessions_wrote_is_declined_to_the_view()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "private int Add(");
        var result = await engine.CheckAsync("Lib/Calc.cs");
        engine.Sessions.Record("A", [engine.Repo.PathOf("Lib/Calc.cs")]);
        engine.Sessions.Record("B", [engine.Repo.PathOf("Lib/Calc.cs")]);

        var error = Assert.Single(result.Errors, e => e.Error.Path == "App/Program.cs").Error;
        var generation = engine.Workspace.PinGeneration();
        var filter = new DeclarationFilter(engine.Workspace, engine.Sessions);

        // The declaring file is credited to two sessions, so no one session's trace decides the error.
        Assert.Equal(
            DeclarationFilter.Decision.Declined,
            await filter.DecideAsync(error, "A", generation, engine.Sessions.WritersOf(generation.Changed), TestContext.Current.CancellationToken));
    }
}
