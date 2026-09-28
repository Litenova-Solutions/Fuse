using Fuse.Check;
using Fuse.Cli;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The line under an error in a file the agent did not edit, naming the declaration that put the file in scope. Without
///     it an agent reading an error in <c>App/Program.cs</c> has to work out which of its own edits caused it.
/// </summary>
public class ErrorContextTests
{
    [Fact]
    public async Task A_renamed_method_names_the_declaration_the_callers_were_using()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        // A rename is a removal of the old declaration and an addition of a new one, and the errors in the callers name
        // the old one, so the line does too.
        var lines = NonNullContexts(report);
        Assert.Equal(report.Introduced.Count(d => d.Path != "Lib/Calc.cs"), lines.Count);
        Assert.All(lines, l => Assert.Equal("removed: public int Add(int a, int b)", l));
        Assert.Equal(
            ["App/Program.cs", "Lib.Tests/CalcTests.cs"],
            report.Introduced.Select(d => d.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_removed_member_names_the_declaration_that_was_at_HEAD()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "    public int Mul(int a, int b) => a * b;\n", "");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        var line = Assert.Single(NonNullContexts(report));
        // The removed declaration is quoted as it was at HEAD, without its body, not as the working tree has it, which is
        // nothing. The arrow is cut with the body, so the line does not end in one.
        Assert.Equal("removed: public int Mul(int a, int b)", line);
    }

    [Fact]
    public async Task A_removed_class_is_named_by_its_header_alone()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Write("Lib/Calc.cs", "namespace Lib;\n\npublic static class Keep\n{\n}\n");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        // The line names a declaration as it was at HEAD, on one line, never the members the class held.
        var lines = NonNullContexts(report);
        Assert.NotEmpty(lines);
        Assert.All(lines, l =>
        {
            Assert.StartsWith("removed: ", l, StringComparison.Ordinal);
            Assert.DoesNotContain("{", l, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", l, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_changed_type_header_names_the_type()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var report = await engine.CheckAsync("Lib/Greeting.cs");

        // The interface member changed, so every call to Greet and every implementation of the interface is in scope.
        var lines = NonNullContexts(report);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Greet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_edit_gets_no_context_because_there_is_no_candidate()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        // The error is in the file the agent edited, so it gets no line either way.
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Equal("Lib/Calc.cs", diagnostic.Path);
        Assert.Empty(NonNullContexts(report));
        Assert.Equal(0, report.ContextLeftOut);
    }

    [Fact]
    public void A_response_is_capped_and_says_how_many_were_left_out()
    {
        // The fixture is too small for the cap to bite in a real check, so the cap is exercised on the function that
        // applies it: 25 candidate errors, each with a declaration to name.
        var provenance = new ReachProvenance();
        var diagnostics = new List<Diagnostic>();
        for (var i = 0; i < 25; i++)
        {
            var path = $"src/Candidate{i}.cs";
            provenance.Add(path, $"public int Method{i}()", removed: false);
            diagnostics.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var (context, leftOut) = ErrorContext.Build(diagnostics, provenance, path => path, []);

        Assert.Equal(diagnostics.Count, context.Length);
        Assert.Equal(10, context.Count(c => c is not null));
        Assert.Equal(15, leftOut);
        Assert.Equal("changed: public int Method0()", context[0]);
    }

    [Fact]
    public async Task A_clean_check_has_no_context_at_all()
    {
        await using var engine = await EngineHarness.StartAsync();
        var report = await engine.CheckAllAsync();

        Assert.Empty(report.Introduced);
        Assert.Empty(NonNullContexts(report));
        Assert.Equal(0, report.ContextLeftOut);
        Assert.Contains("no new errors", CheckOperation.Render(report).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_context_line_is_indented_under_its_diagnostic()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        var lines = CheckOperation.Render(report).Text.Split('\n');
        var errorIndex = Array.FindIndex(lines, l => l.Contains("CS1061", StringComparison.Ordinal));
        Assert.True(errorIndex >= 0, $"no error line in: {string.Join(" | ", lines)}");
        Assert.Equal("  removed: public int Add(int a, int b)", lines[errorIndex + 1]);
    }

    private static List<string> NonNullContexts(CheckReport report) =>
        report.Context is null ? [] : [.. report.Context.OfType<string>()];
}
