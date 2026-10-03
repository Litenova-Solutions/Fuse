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
    // The cases that call CauseLines directly name files but read none, so any root will do.
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;

    [Fact]
    public async Task A_renamed_method_names_the_declaration_the_callers_were_using()
    {
        await using var engine = await InProcessEngine.StartAsync();
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
        await using var engine = await InProcessEngine.StartAsync();
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
        await using var engine = await InProcessEngine.StartAsync();
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
    public async Task A_changed_interface_member_names_the_member()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var result = await engine.CheckAsync("Lib/Greeting.cs");

        // The interface member changed, so every call to Greet and every implementation of the interface is a candidate. The
        // parameter list is part of the key, so the change is the removal of the old member, which the callers used. A
        // changed type header is broad and gets no cause at all.
        var lines = PrintedCauses(result);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal("removed: string Greet(string name)", l));
    }

    [Fact]
    public async Task A_changed_field_type_names_the_field_s_declaration_without_its_initializer()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/Config.cs", "namespace Lib;\n\npublic class Config\n{\n    public int Limit = Compute();\n\n    private static int Compute() => 5;\n}\n");
        repo.Write("App/UseConfig.cs", "namespace App;\n\npublic static class UseConfig\n{\n    public static int Run()\n    {\n        int limit = new Lib.Config().Limit;\n        return limit;\n    }\n}\n");
        repo.Commit("config");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Config.cs", "public int Limit = Compute();\n\n    private static int Compute()", "public long Limit = Compute();\n\n    private static long Compute()");
        var result = await engine.CheckAsync("Lib/Config.cs");

        // The line shows what changed, the field's type, where quoting the declarator gave "Limit = Compute()" both times.
        Assert.Equal("changed: public long Limit", Assert.Single(PrintedCauses(result)));
    }

    [Fact]
    public async Task A_body_edit_gets_no_cause_because_there_is_no_candidate()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        // The error is in the file the agent edited, so it gets no line either way.
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lib/Calc.cs", error.Error.Path);
        Assert.Null(error.Cause);
        Assert.Empty(PrintedCauses(result));
        Assert.False(error.IsCauseLeftOut);
    }

    [Fact]
    public void An_answer_is_capped_and_says_how_many_were_left_out()
    {
        // The fixture is too small for the cap to bite in a real check, so the cap is exercised on the function that
        // applies it: 25 candidate errors, each with a declaration to name.
        var causes = new Dictionary<RepoPath, Cause>();
        var errors = new List<CompilerError>();
        for (var i = 0; i < 25; i++)
        {
            var path = $"src/Candidate{i}.cs";
            causes[Root.PathOf(path)] = new Cause.Changed($"public int Method{i}()");
            errors.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var attached = CauseLines.Attach(errors, new Reach.Precise(causes), Root, []);

        Assert.Equal(errors, attached.Select(e => e.Error));
        Assert.Equal(CauseLines.MaxCauses, attached.Count(e => e.Cause is not null));
        Assert.All(attached.Take(CauseLines.MaxCauses), e => Assert.NotNull(e.Cause));
        Assert.All(attached.Skip(CauseLines.MaxCauses), e => Assert.Null(e.Cause));
        Assert.All(attached.Skip(CauseLines.MaxCauses), e => Assert.True(e.IsCauseLeftOut));
        Assert.Equal(15, attached.Count(e => e.IsCauseLeftOut));
        Assert.Equal(new Cause.Changed("public int Method0()"), attached[0].Cause);
    }

    [Fact]
    public void A_target_an_analyzer_error_and_a_file_nothing_reached_get_no_cause_and_do_not_count_toward_the_cap()
    {
        // Every path here has a cause in the reach, so only the rules, not a missing entry, keep the first three bare.
        var causes = new Dictionary<RepoPath, Cause>
        {
            [Root.PathOf("src/Target.cs")] = new Cause.Changed("public int Target()"),
            [Root.PathOf("src/Analyzed.cs")] = new Cause.Changed("public int Analyzed()"),
        };
        var errors = new List<CompilerError>
        {
            new("src/Target.cs", 1, 1, "CS0103", "in a target"),
            new("src/Analyzed.cs", 1, 1, "CA1825", "from an analyzer", FromAnalyzer: true),
            new("src/Unreached.cs", 1, 1, "CS0103", "in a file no change reached"),
        };
        for (var i = 0; i < CauseLines.MaxCauses + 2; i++)
        {
            var path = $"src/Candidate{i}.cs";
            causes[Root.PathOf(path)] = new Cause.Removed($"public int Method{i}()");
            errors.Add(new(path, 1, 1, "CS1061", "no definition"));
        }

        var attached = CauseLines.Attach(errors, new Reach.Precise(causes), Root, [Root.PathOf("src/Target.cs")]);

        Assert.All(attached.Take(3), e => Assert.Null(e.Cause));
        Assert.Equal(CauseLines.MaxCauses, attached.Count(e => e.Cause is not null));
        Assert.Equal(new Cause.Removed("public int Method0()"), attached[3].Cause);
        Assert.All(attached.Take(3), e => Assert.False(e.IsCauseLeftOut));
        Assert.Equal(2, attached.Count(e => e.IsCauseLeftOut));
    }

    [Fact]
    public void A_reach_that_is_not_precise_gives_no_cause()
    {
        List<CompilerError> errors = [new("src/Candidate.cs", 1, 1, "CS1061", "no definition")];
        var files = new HashSet<RepoPath> { Root.PathOf("src/Candidate.cs") };

        foreach (var reach in new Reach[] { new Reach.None(), new Reach.Broad(files) })
        {
            var attached = Assert.Single(CauseLines.Attach(errors, reach, Root, []));
            Assert.Null(attached.Cause);
            Assert.False(attached.IsCauseLeftOut);
        }
    }

    [Fact]
    public async Task A_clean_check_has_no_cause_at_all()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var result = await engine.CheckAllAsync();

        Assert.Empty(result.Errors);
        Assert.Empty(PrintedCauses(result));
        Assert.Contains("no errors introduced", Render(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cause_line_is_indented_under_its_error()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var result = await engine.CheckAsync("Lib/Calc.cs");

        var lines = Render(result).Split('\n');
        var errorIndex = Array.FindIndex(lines, l => l.Contains("CS1061", StringComparison.Ordinal));
        Assert.True(errorIndex >= 0, $"no error line in: {string.Join(" | ", lines)}");
        Assert.Equal("  removed: public int Add(int a, int b)", lines[errorIndex + 1]);
    }

    [Fact]
    public async Task An_error_in_a_file_two_changes_reach_names_the_change_it_is_about()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // App/Program.cs calls both Calc.Add and IGreeter.Greet, so both renames reach it.
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        var result = await engine.CheckAllAsync();

        Assert.Equal(new Cause.Removed("public int Add(int a, int b)"), CauseOf(result, "App/Program.cs", "'Add'"));
        Assert.Equal(new Cause.Removed("string Greet(string name)"), CauseOf(result, "App/Program.cs", "'Greet'"));
    }

    [Theory]
    [InlineData("public int Add(int a, int b)", "Add")]
    [InlineData("public T Get<T>(int index) where T : class", "Get")]
    [InlineData("public System.Collections.Generic.List<int> Items { get; }", "Items")]
    [InlineData("public sealed class Repository<TKey, TValue> : IRepository<TKey>", "Repository")]
    [InlineData("public const int Limit = 3", "Limit")]
    [InlineData("public int this[int index]", "this")]
    [InlineData("public record Point(int X, int Y)", "Point")]
    public void A_declaration_is_named_as_compiler_messages_quote_it(string declaration, string name) =>
        Assert.Equal(name, CauseLines.NameOf(declaration));

    [Fact]
    public void An_error_naming_no_declaration_keeps_the_first_cause()
    {
        Cause add = new Cause.Removed("public int Add(int a, int b)");
        Cause greet = new Cause.Removed("string Greet(string name)");
        var error = new CompilerError("App/Program.cs", 1, 1, "CS0029", "Cannot implicitly convert type 'int' to 'string'");

        Assert.Equal(add, CauseLines.About(error, add, [add, greet]));
        // 'IGreeter' holds Greet, but not as a whole word, so it does not name the method.
        Assert.Equal(add, CauseLines.About(error with { Message = "'IGreeter' is inaccessible" }, add, [add, greet]));
        Assert.Equal(greet, CauseLines.About(error with { Message = "'IGreeter.Greet(string)' is inaccessible" }, add, [add, greet]));
    }

    private static Cause? CauseOf(CheckResult result, string path, string quoted) =>
        Assert.Single(result.Errors, e => e.Error.Path == path && e.Error.Message.Contains(quoted, StringComparison.Ordinal)).Cause;

    /// <summary>What the client prints for <paramref name="result"/>, after the engine maps it to the wire.</summary>
    private static string Render(CheckResult result) => CheckOperation.Render(ResponseMapper.Report(result)).Text;

    /// <summary>The cause lines the client prints, each without the two spaces that indent it under its error.</summary>
    private static List<string> PrintedCauses(CheckResult result) =>
        [.. Render(result).Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l[2..])];
}
