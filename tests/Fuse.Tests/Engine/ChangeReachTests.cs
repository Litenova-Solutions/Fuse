using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     How a target's declaration changes become a <see cref="Reach"/>: whether a target has a declaration change at all,
///     and when it does, whether the files it can break are named one by one with their causes or are every file in the
///     reached projects.
/// </summary>
public class ChangeReachTests
{
    [Fact]
    public async Task A_body_edit_and_an_added_using_are_not_declaration_changes()
    {
        await using var engine = await EngineHarness.StartAsync();
        var reach = new ChangeReach(engine.Workspace);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Replace("Lib/Formatter.cs", "namespace Lib;", "using System.Text;\n\nnamespace Lib;");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        await engine.CheckAsync("Lib/Calc.cs", "Lib/Formatter.cs", "Lib/Greeting.cs");

        Assert.False(await reach.HasDeclarationChangeAsync(engine.Repo.Full("Lib/Calc.cs"), TestContext.Current.CancellationToken));
        Assert.False(await reach.HasDeclarationChangeAsync(engine.Repo.Full("Lib/Formatter.cs"), TestContext.Current.CancellationToken));
        Assert.True(await reach.HasDeclarationChangeAsync(engine.Repo.Full("Lib/Greeting.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_renamed_member_reaches_its_callers_with_the_declaration_that_was_removed()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.CheckAsync("Lib/Calc.cs");

        var precise = Assert.IsType<Reach.Precise>(await engine.ReachAsync("Lib/Calc.cs"));

        Assert.Equal([engine.Repo.Full("App/Program.cs"), engine.Repo.Full("Lib.Tests/CalcTests.cs")], precise.Causes.Keys.Order(StringComparer.Ordinal));
        Assert.All(precise.Causes.Values, c => Assert.Equal(new Cause.Removed("public int Add(int a, int b)"), c));
    }

    [Fact]
    public async Task An_added_overload_reaches_the_callers_of_the_same_name_with_the_declaration_that_was_added()
    {
        await using var engine = await EngineHarness.StartAsync();
        // A new overload can make an existing call ambiguous, so the callers of Add are candidates though none names it.
        engine.Repo.Replace("Lib/Calc.cs", "    public int Mul(", "    public long Add(long a, long b) => a + b;\n\n    public int Mul(");
        await engine.CheckAsync("Lib/Calc.cs");

        var precise = Assert.IsType<Reach.Precise>(await engine.ReachAsync("Lib/Calc.cs"));

        Assert.Equal([engine.Repo.Full("App/Program.cs"), engine.Repo.Full("Lib.Tests/CalcTests.cs")], precise.Causes.Keys.Order(StringComparer.Ordinal));
        Assert.All(precise.Causes.Values, c => Assert.Equal(new Cause.Changed("public long Add(long a, long b)"), c));
    }

    [Fact]
    public async Task A_changed_type_header_is_broad_and_reaches_files_that_never_name_the_type()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public class Calc", "public sealed class Calc");
        await engine.CheckAsync("Lib/Calc.cs");

        var broad = Assert.IsType<Reach.Broad>(await engine.ReachAsync("Lib/Calc.cs"));

        // Report.cs and GreeterTests.cs never mention Calc; a broad change binds them anyway.
        Assert.Contains(engine.Repo.Full("App/Report.cs"), broad.Files);
        Assert.Contains(engine.Repo.Full("Lib.Tests/GreeterTests.cs"), broad.Files);
        Assert.Contains(engine.Repo.Full("App.Tests/ReportTests.cs"), broad.Files);
        // Only the projects with declaration changes and their dependents are reached.
        Assert.DoesNotContain(engine.Repo.Full("Strict/Thing.cs"), broad.Files);
    }
}
