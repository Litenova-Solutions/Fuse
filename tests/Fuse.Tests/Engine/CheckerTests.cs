using Fuse.Check;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

public class CheckerTests
{
    [Fact]
    public async Task Clean_tree_reports_nothing()
    {
        await using var engine = await EngineHarness.StartAsync();
        var report = await engine.CheckAllAsync();
        Assert.Empty(report.Introduced);
        Assert.Equal(0, report.FilesChecked);
    }

    [Fact]
    public async Task Body_edit_checks_only_the_edited_file()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Empty(report.Introduced);
        Assert.Empty(report.SurfaceChangedIn);
        Assert.Equal(0, report.DependentProjectsChecked);
        Assert.Equal(1, report.FilesChecked);
    }

    [Fact]
    public async Task Body_error_is_reported_in_the_edited_file()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Equal("Lib/Calc.cs", diagnostic.Path);
        Assert.Equal("CS0103", diagnostic.Id);
        Assert.Equal(7, diagnostic.Line);
    }

    [Fact]
    public async Task Renamed_member_breaks_dependent_projects()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], report.Introduced.Select(d => d.Path).Order(StringComparer.Ordinal));
        Assert.All(report.Introduced, d => Assert.Equal("CS1061", d.Id));
        Assert.Equal(["Lib"], report.SurfaceChangedIn);
        Assert.True(report.DependentProjectsChecked >= 2);
    }

    [Fact]
    public async Task Removed_member_breaks_callers()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "    public int Mul(int a, int b) => a * b;\n", "");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Equal("Lib.Tests/CalcTests.cs", diagnostic.Path);
    }

    [Fact]
    public async Task Changed_signature_breaks_callers()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var report = await engine.CheckAsync("Lib/Greeting.cs");
        var paths = report.Introduced.Select(d => d.Path).Distinct().Order(StringComparer.Ordinal).ToList();
        // Greeter's method does not match the interface, and both callers pass too few arguments.
        Assert.Equal(["App/Program.cs", "Lib.Tests/GreeterTests.cs", "Lib/Greeting.cs"], paths);
    }

    [Fact]
    public async Task Overloads_that_make_calls_ambiguous_are_reported()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace(
            "Lib/Calc.cs",
            "public int Add(int a, int b) => a + b;",
            "public long Add(long a, int b) => a + b;\n\n    public long Add(int a, long b) => a + b;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Introduced, d => d.Id == "CS0121" && d.Path == "App/Program.cs");
        Assert.Contains(report.Introduced, d => d.Id == "CS0121" && d.Path == "Lib.Tests/CalcTests.cs");
    }

    [Fact]
    public async Task New_file_with_an_error_is_reported()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => missing;\n}\n");
        var report = await engine.CheckAsync("Lib/Extra.cs");
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Equal("Lib/Extra.cs", diagnostic.Path);
        Assert.Equal("CS0103", diagnostic.Id);
    }

    [Fact]
    public async Task New_file_used_by_another_edit_is_visible()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => 42;\n}\n");
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value=\" + Lib.Formatter.Format(value + Lib.Extra.Value())");
        var report = await engine.CheckAsync("Lib/Extra.cs", "App/Report.cs");
        Assert.Empty(report.Introduced);
    }

    [Fact]
    public async Task Deleted_file_breaks_its_users()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Delete("Lib/Formatter.cs");
        var report = await engine.CheckAsync("Lib/Formatter.cs");
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Equal("App/Report.cs", diagnostic.Path);
        Assert.Equal("CS0234", diagnostic.Id);
    }

    [Fact]
    public async Task Multi_edit_sequence_tracks_the_latest_state()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var broken = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Equal(2, broken.Introduced.Length);

        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Plus(1, 2)");
        var partly = await engine.CheckAsync("App/Program.cs");
        Assert.Empty(partly.Introduced);
        var stillBroken = await engine.CheckAllAsync();
        Assert.Equal("Lib.Tests/CalcTests.cs", Assert.Single(stillBroken.Introduced).Path);

        engine.Repo.Replace("Lib.Tests/CalcTests.cs", "NewCalc().Add(1, 2)", "NewCalc().Plus(1, 2)");
        var fixedReport = await engine.CheckAllAsync();
        Assert.Empty(fixedReport.Introduced);
    }

    [Fact]
    public async Task Fixing_an_error_returns_clean()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * nope;");
        Assert.Single((await engine.CheckAsync("Lib/Calc.cs")).Introduced);
        engine.Repo.Replace("Lib/Calc.cs", "a * nope;", "a * b;");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Introduced);
    }

    [Fact]
    public async Task Two_edits_in_different_projects_reach_disjoint_candidates()
    {
        await using var engine = await EngineHarness.StartAsync();
        // Report.Line is called by App.Tests and Calc.Add by App and Lib.Tests, so each edit reaches its own files.
        engine.Repo.Replace("App/Report.cs", "Line(int value)", "Line2(int value)");
        engine.Repo.Replace("Lib/Calc.cs", "Add(int a, int b)", "Add2(int a, int b)");

        // The provenance queries run after a real check, so the HEAD view is loaded the way the check loads it.
        await engine.CheckAsync("App/Report.cs");
        var fromApp = await CandidatesAsync(engine, "App/Report.cs");
        var fromLib = await CandidatesAsync(engine, "Lib/Calc.cs");

        Assert.Equal([engine.Repo.Full("App.Tests/ReportTests.cs")], fromApp);
        Assert.Equal([engine.Repo.Full("App/Program.cs"), engine.Repo.Full("Lib.Tests/CalcTests.cs")], fromLib);
        Assert.Empty(fromApp.Intersect(fromLib, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Each_candidate_names_the_declaration_that_reached_it()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var report = await engine.CheckAsync("Lib/Calc.cs", "Lib/Greeting.cs");

        var lines = report.Context?.OfType<string>().ToList() ?? [];
        Assert.Contains(lines, l => l.Contains("Add", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Greet", StringComparison.Ordinal) && l.StartsWith("removed: ", StringComparison.Ordinal));
    }

    /// <summary>The files the change in <paramref name="relative"/> puts in scope, the way the checker computes them.</summary>
    private static async Task<List<string>> CandidatesAsync(EngineHarness engine, string relative)
    {
        var graph = engine.Workspace.Graph;
        var path = engine.Repo.Full(relative);
        var owners = graph.OwnersOf(path).ToList();
        await engine.Workspace.EnsureLoadedAsync(owners, TestContext.Current.CancellationToken);
        var dependents = owners.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
            .Where(p => !owners.Any(o => string.Equals(o.Path, p.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        await engine.Workspace.EnsureLoadedAsync(dependents, TestContext.Current.CancellationToken);
        var reach = owners.Concat(dependents).SelectMany(n => RepoWorkspace.ProjectsFor(engine.Workspace.Current, n)).ToList();
        var provenance = await new ChangeReach(engine.Workspace).ProvenanceAsync([path], reach, TestContext.Current.CancellationToken);
        return provenance?.Files.Order(StringComparer.Ordinal).ToList() ?? [];
    }

    [Fact]
    public async Task Errors_already_present_at_head_are_not_reported()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("commit a broken file");
        var atHead = await engine.CheckAllAsync();
        Assert.Empty(atHead.Introduced);

        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + anotherError;");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        var diagnostic = Assert.Single(report.Introduced);
        Assert.Contains("anotherError", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warning_promoted_by_TreatWarningsAsErrors_is_reported()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value()\n    {\n        int unused = 0;\n        return 1;\n    }");
        var report = await engine.CheckAsync("Strict/Thing.cs");
        Assert.Equal("CS0219", Assert.Single(report.Introduced).Id);
    }

    [Fact]
    public async Task Analyzer_raised_to_error_by_editorconfig_is_reported()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + 1;");
        var report = await engine.CheckAsync("Strict/Thing.cs");
        Assert.Equal("CA1825", Assert.Single(report.Introduced).Id);
    }

    [Fact]
    public async Task Error_in_multi_targeted_project_is_reported_once()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Multi/Shape.cs", "=> count;", "=> count + missing;");
        var report = await engine.CheckAsync("Multi/Shape.cs");
        Assert.Equal("CS0103", Assert.Single(report.Introduced).Id);
    }

    [Fact]
    public async Task Project_file_change_is_picked_up()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Mul(", "#if FUSE_FLAG\n    public int Broken => missingSymbol;\n#endif\n\n    public int Mul(");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Introduced);

        engine.Repo.Replace("Lib/Lib.csproj", "<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net10.0</TargetFramework><DefineConstants>$(DefineConstants);FUSE_FLAG</DefineConstants>");
        await Task.Delay(600, TestContext.Current.CancellationToken);
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Contains(report.Introduced, d => d.Message.Contains("missingSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Branch_switch_rebases_the_baseline()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        Assert.NotEmpty((await engine.CheckAsync("Lib/Calc.cs")).Introduced);

        // Committing the rename moves HEAD: the callers' errors are in the baseline, so they are not introduced.
        engine.Repo.Commit("rename");
        var report = await engine.CheckAllAsync();
        Assert.Empty(report.Introduced);
    }

    [Fact]
    public async Task Restore_needed_is_reported_with_the_fix()
    {
        await using var engine = await EngineHarness.StartAsync();
        File.Delete(engine.Repo.Full("Lib/obj/project.assets.json"));
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var error = await Assert.ThrowsAsync<Fuse.Workspace.FuseException>(() => engine.CheckAsync("Lib/Calc.cs"));
        Assert.Equal(ErrorCode.RestoreNeeded, error.Code);
        Assert.Contains("dotnet restore Lib/Lib.csproj", error.Message, StringComparison.Ordinal);
    }
}
