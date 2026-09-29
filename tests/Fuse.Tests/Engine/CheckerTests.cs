using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class CheckerTests
{
    [Fact]
    public async Task Clean_tree_reports_nothing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var report = await engine.CheckAllAsync();
        Assert.Empty(report.Errors);
        Assert.Equal(0, report.FilesChecked);
    }

    [Fact]
    public async Task Body_edit_checks_only_the_edited_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Empty(report.Errors);
        Assert.Empty(report.DeclarationsChangedIn);
        Assert.Equal(0, report.DependentProjectsChecked);
        Assert.Equal(1, report.FilesChecked);
    }

    [Fact]
    public async Task Body_error_is_reported_in_the_edited_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("Lib/Calc.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
        Assert.Equal(7, error.Line);
    }

    [Fact]
    public async Task Renamed_member_breaks_dependent_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], report.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
        Assert.All(report.Errors, e => Assert.Equal("CS1061", e.Error.Id));
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        Assert.True(report.DependentProjectsChecked >= 2);
    }

    [Fact]
    public async Task Removed_member_breaks_callers()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "    public int Mul(int a, int b) => a * b;\n", "");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("Lib.Tests/CalcTests.cs", error.Path);
    }

    [Fact]
    public async Task Changed_signature_breaks_callers()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var report = await engine.CheckAsync("Lib/Greeting.cs");
        var paths = report.Errors.Select(e => e.Error.Path).Distinct().Order(StringComparer.Ordinal).ToList();
        // Greeter's method does not match the interface, and both callers pass too few arguments.
        Assert.Equal(["App/Program.cs", "Lib.Tests/GreeterTests.cs", "Lib/Greeting.cs"], paths);
    }

    [Fact]
    public async Task Overloads_that_make_calls_ambiguous_are_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace(
            "Lib/Calc.cs",
            "public int Add(int a, int b) => a + b;",
            "public long Add(long a, int b) => a + b;\n\n    public long Add(int a, long b) => a + b;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Errors, e => e.Error.Id == "CS0121" && e.Error.Path == "App/Program.cs");
        Assert.Contains(report.Errors, e => e.Error.Id == "CS0121" && e.Error.Path == "Lib.Tests/CalcTests.cs");
    }

    [Fact]
    public async Task New_file_with_an_error_is_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => missing;\n}\n");
        var report = await engine.CheckAsync("Lib/Extra.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("Lib/Extra.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
    }

    [Fact]
    public async Task New_file_used_by_another_edit_is_visible()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => 42;\n}\n");
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value=\" + Lib.Formatter.Format(value + Lib.Extra.Value())");
        var report = await engine.CheckAsync("Lib/Extra.cs", "App/Report.cs");
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task Deleted_file_breaks_its_users()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Delete("Lib/Formatter.cs");
        var report = await engine.CheckAsync("Lib/Formatter.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("App/Report.cs", error.Path);
        Assert.Equal("CS0234", error.Id);
    }

    [Fact]
    public async Task Multi_edit_sequence_tracks_the_latest_state()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var broken = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Equal(2, broken.Errors.Count);

        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Plus(1, 2)");
        var partly = await engine.CheckAsync("App/Program.cs");
        Assert.Empty(partly.Errors);
        var stillBroken = await engine.CheckAllAsync();
        Assert.Equal("Lib.Tests/CalcTests.cs", Assert.Single(stillBroken.Errors).Error.Path);

        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "NewCalc().Add(1, 2)", "NewCalc().Plus(1, 2)");
        var fixedReport = await engine.CheckAllAsync();
        Assert.Empty(fixedReport.Errors);
    }

    [Fact]
    public async Task Fixing_an_error_returns_clean()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * nope;");
        Assert.Single((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        engine.Repo.Replace("Lib/Calc.cs", "a * nope;", "a * b;");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
    }

    [Fact]
    public async Task Two_edits_in_different_projects_reach_disjoint_candidates()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // Report.Line is called by App.Tests and Calc.Add by App and Lib.Tests, so each edit reaches its own files.
        engine.Repo.Replace("App/Report.cs", "Line(int value)", "Line2(int value)");
        engine.Repo.Replace("Lib/Calc.cs", "Add(int a, int b)", "Add2(int a, int b)");

        // The reach queries run after a real check, so the HEAD view is loaded the way the check loads it.
        await engine.CheckAsync("App/Report.cs");
        var fromApp = await CandidatesAsync(engine, "App/Report.cs");
        var fromLib = await CandidatesAsync(engine, "Lib/Calc.cs");

        Assert.Equal([engine.Repo.Full("App.Tests/ReportTests.cs")], fromApp.Select(p => p.Absolute));
        Assert.Equal([engine.Repo.Full("App/Program.cs"), engine.Repo.Full("Lib.Tests/CalcTests.cs")], fromLib.Select(p => p.Absolute));
        Assert.Empty(fromApp.Intersect(fromLib));
    }

    [Fact]
    public async Task Each_candidate_names_the_declaration_that_reached_it()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var report = await engine.CheckAsync("Lib/Calc.cs", "Lib/Greeting.cs");

        var causes = report.Errors.Select(e => e.Cause).OfType<Cause>().ToList();
        Assert.Contains(causes, c => c.Declaration.Contains("Add", StringComparison.Ordinal));
        Assert.Contains(causes, c => c.Declaration.Contains("Greet", StringComparison.Ordinal) && c is Cause.Removed);
    }

    /// <summary>The files the change in <paramref name="relative"/> puts in scope, the way the checker computes them.</summary>
    private static async Task<List<RepoPath>> CandidatesAsync(InProcessEngine engine, string relative) =>
        [.. Assert.IsType<Reach.Precise>(await engine.ReachAsync(relative)).Causes.Keys.OrderBy(k => k.Absolute, StringComparer.Ordinal)];

    [Fact]
    public async Task Errors_already_present_at_head_are_not_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("commit a broken file");
        var atHead = await engine.CheckAllAsync();
        Assert.Empty(atHead.Errors);

        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + anotherError;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Contains("anotherError", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warning_promoted_by_TreatWarningsAsErrors_is_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value()\n    {\n        int unused = 0;\n        return 1;\n    }");
        var report = await engine.CheckAsync("Strict/Thing.cs");
        Assert.Equal("CS0219", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task Analyzer_raised_to_error_by_editorconfig_is_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + 1;");
        var report = await engine.CheckAsync("Strict/Thing.cs");
        Assert.Equal("CA1825", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task Error_in_multi_targeted_project_is_reported_once()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Multi/Shape.cs", "=> count;", "=> count + missing;");
        var report = await engine.CheckAsync("Multi/Shape.cs");
        Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task Project_file_change_is_picked_up()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Mul(", "#if FUSE_FLAG\n    public int Broken => missingSymbol;\n#endif\n\n    public int Mul(");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);

        engine.Repo.Replace("Lib/Lib.csproj", "<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net10.0</TargetFramework><DefineConstants>$(DefineConstants);FUSE_FLAG</DefineConstants>");
        await Task.Delay(600, TestContext.Current.CancellationToken);
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Errors, e => e.Error.Message.Contains("missingSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Branch_switch_rebases_the_baseline()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        Assert.NotEmpty((await engine.CheckAsync("Lib/Calc.cs")).Errors);

        // Committing the rename moves HEAD: the callers' errors are in the baseline, so they are not introduced.
        engine.Repo.Commit("rename");
        var report = await engine.CheckAllAsync();
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task A_removed_using_checks_the_files_that_use_the_file_s_types()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // System.Collections.ObjectModel is not an implicit using, so the interface's signature depends on this directive.
        engine.Repo.Write("Lib/Runner.cs", "using System.Collections.ObjectModel;\n\nnamespace Lib;\n\npublic interface IRunner\n{\n    ReadOnlyCollection<int> Run();\n}\n");
        engine.Repo.Write("Lib/RunnerImpl.cs", "namespace Lib;\n\npublic sealed class Runner : IRunner\n{\n    public System.Collections.ObjectModel.ReadOnlyCollection<int> Run() => new(System.Array.Empty<int>());\n}\n");
        engine.Repo.Commit("runner");

        // The declaration's text is unchanged, but its return type no longer resolves, so the implementation breaks.
        engine.Repo.Replace("Lib/Runner.cs", "using System.Collections.ObjectModel;\n", "");
        var report = await engine.CheckAsync("Lib/Runner.cs");

        Assert.Contains(report.Errors, e => e.Error.Path == "Lib/Runner.cs" && e.Error.Id == "CS0246");
        var implementation = report.Errors.FirstOrDefault(e => e.Error.Path == "Lib/RunnerImpl.cs");
        Assert.True(implementation is not null, $"the implementation's break was not reported: {string.Join("; ", report.Errors.Select(e => e.Error.ToString()))}");
        Assert.Equal(new Cause.Removed("using System.Collections.ObjectModel;"), implementation.Cause);
    }

    [Fact]
    public async Task An_added_using_does_not_reach_other_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "namespace Lib;", "using System.Text;\n\nnamespace Lib;");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.Empty(report.DeclarationsChangedIn);
        Assert.Equal(0, report.DependentProjectsChecked);
    }

    [Fact]
    public async Task Restore_needed_is_reported_with_the_fix()
    {
        await using var engine = await InProcessEngine.StartAsync();
        File.Delete(engine.Repo.Full("Lib/obj/project.assets.json"));
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var error = await Assert.ThrowsAsync<FuseException>(() => engine.CheckAsync("Lib/Calc.cs"));
        Assert.Equal(ErrorCode.RestoreNeeded, error.Code);
        Assert.Contains("dotnet restore Lib/Lib.csproj", error.Message, StringComparison.Ordinal);
    }
}
