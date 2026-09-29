using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The line under an error in a file the agent did not edit, naming the declaration change that put the file in the
///     check. Without it an agent reading an error in <c>App/Program.cs</c> has to work out which of its own edits caused
///     it.
/// </summary>
public class CauseLinesTests
{
    [Fact]
    public async Task A_renamed_method_names_the_declaration_the_callers_were_using()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        // A rename is a removal of the old declaration and an addition of a new one, and the errors in the callers name
        // the old one, so the line does too.
        var lines = PrintedCauses(result);
        Assert.Equal(result.Errors.Count(e => e.Error.Path != "Lib/Calc.cs"), lines.Count);
        Assert.All(lines, l => Assert.Equal("removed: public int Add(int a, int b)", l));
        Assert.Equal(
            ["App/Program.cs", "Lib.Tests/CalcTests.cs"],
            result.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_removed_member_names_the_declaration_that_was_at_HEAD()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "    public int Mul(int a, int b) => a * b;\n", "");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        var line = Assert.Single(PrintedCauses(result));
        // The removed declaration is quoted as it was at HEAD, without its body, not as the working tree has it, which is
        // nothing. The arrow is cut with the body, so the line does not end in one.
        Assert.Equal("removed: public int Mul(int a, int b)", line);
    }

    [Fact]
    public async Task A_removed_class_is_named_by_its_header_alone()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Write("Lib/Calc.cs", "namespace Lib;\n\npublic static class Keep\n{\n}\n");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        // The line names a declaration as it was at HEAD, on one line, never the members the class held.
        var lines = PrintedCauses(result);
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
        var result = await engine.CheckAsync("Lib/Greeting.cs");

        // The interface member changed, so every call to Greet and every implementation of the interface is in scope.
        var lines = PrintedCauses(result);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Greet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_edit_gets_no_cause_because_there_is_no_candidate()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        // The error is in the file the agent edited, so it gets no line either way.
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lib/Calc.cs", error.Error.Path);
        Assert.Null(error.Cause);
        Assert.Empty(PrintedCauses(result));
        Assert.Equal(0, result.CausesLeftOut);
    }

    [Fact]
    public void An_answer_is_capped_and_says_how_many_were_left_out()
    {
        // The fixture is too small for the cap to bite in a real check, so the cap is exercised on the function that
        // applies it: 25 candidate errors, each with a declaration to name.
        var causes = new Dictionary<string, Cause>(PathRules.PathComparer);
        var errors = new List<CompilerError>();
        for (var i = 0; i < 25; i++)
        {
            var path = $"src/Candidate{i}.cs";
            causes[path] = new Cause.Changed($"public int Method{i}()");
            errors.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var (attached, leftOut) = CauseLines.Attach(errors, new Reach.Precise(causes), path => path, []);

        Assert.Equal(errors, attached.Select(e => e.Error));
        Assert.Equal(CauseLines.MaxLines, attached.Count(e => e.Cause is not null));
        Assert.All(attached.Take(CauseLines.MaxLines), e => Assert.NotNull(e.Cause));
        Assert.All(attached.Skip(CauseLines.MaxLines), e => Assert.Null(e.Cause));
        Assert.Equal(15, leftOut);
        Assert.Equal(new Cause.Changed("public int Method0()"), attached[0].Cause);
    }

    [Fact]
    public void A_target_an_analyzer_error_and_a_file_nothing_reached_get_no_cause_and_do_not_count_toward_the_cap()
    {
        // Every path here has a cause in the reach, so only the rules, not a missing entry, keep the first three bare.
        var causes = new Dictionary<string, Cause>(PathRules.PathComparer)
        {
            ["src/Target.cs"] = new Cause.Changed("public int Target()"),
            ["src/Analyzed.cs"] = new Cause.Changed("public int Analyzed()"),
        };
        var errors = new List<CompilerError>
        {
            new("src/Target.cs", 1, 1, "CS0103", "in a target"),
            new("src/Analyzed.cs", 1, 1, "CA1825", "from an analyzer", FromAnalyzer: true),
            new("src/Unreached.cs", 1, 1, "CS0103", "in a file no change reached"),
        };
        for (var i = 0; i < CauseLines.MaxLines + 2; i++)
        {
            var path = $"src/Candidate{i}.cs";
            causes[path] = new Cause.Removed($"public int Method{i}()");
            errors.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var (attached, leftOut) = CauseLines.Attach(errors, new Reach.Precise(causes), path => path, ["src/Target.cs"]);

        Assert.All(attached.Take(3), e => Assert.Null(e.Cause));
        Assert.Equal(CauseLines.MaxLines, attached.Count(e => e.Cause is not null));
        Assert.Equal(new Cause.Removed("public int Method0()"), attached[3].Cause);
        Assert.Equal(2, leftOut);
    }

    [Fact]
    public void A_reach_that_is_not_precise_gives_no_cause()
    {
        List<CompilerError> errors = [new("src/Candidate.cs", 1, 1, "CS1061", "no definition")];
        var files = new HashSet<string>(PathRules.PathComparer) { "src/Candidate.cs" };

        foreach (var reach in new Reach[] { new Reach.None(), new Reach.Broad(files) })
        {
            var (attached, leftOut) = CauseLines.Attach(errors, reach, path => path, []);
            Assert.Null(Assert.Single(attached).Cause);
            Assert.Equal(0, leftOut);
        }
    }

    [Fact]
    public async Task A_clean_check_has_no_cause_at_all()
    {
        await using var engine = await EngineHarness.StartAsync();
        var result = await engine.CheckAllAsync();

        Assert.Empty(result.Errors);
        Assert.Empty(PrintedCauses(result));
        Assert.Equal(0, result.CausesLeftOut);
        Assert.Contains("no errors introduced", Render(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cause_line_is_indented_under_its_error()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        var lines = Render(result).Split('\n');
        var errorIndex = Array.FindIndex(lines, l => l.Contains("CS1061", StringComparison.Ordinal));
        Assert.True(errorIndex >= 0, $"no error line in: {string.Join(" | ", lines)}");
        Assert.Equal("  removed: public int Add(int a, int b)", lines[errorIndex + 1]);
    }

    /// <summary>What the client prints for <paramref name="result"/>, after the engine maps it to the wire.</summary>
    private static string Render(CheckResult result) => CheckOperation.Render(ResponseMapper.Report(result)).Text;

    /// <summary>The cause lines the client prints, each without the two spaces that indent it under its error.</summary>
    private static List<string> PrintedCauses(CheckResult result) =>
        [.. Render(result).Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l[2..])];
}
