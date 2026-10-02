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
    public async Task Errors_past_the_cap_are_still_counted()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var lines = string.Concat(Enumerable.Range(1, 205).Select(i => $"    public static int F{i}() => missing{i};\n"));
        engine.Repo.Write("Lib/Many.cs", $"namespace Lib;\n\npublic static class Many\n{{\n{lines}}}\n");
        engine.Repo.Write("Lib/Other.cs", "namespace Lib;\n\npublic static class Other\n{\n    public static int G() => missing;\n}\n");
        var report = await engine.CheckAllAsync();
        Assert.Equal(200, report.Errors.Count);
        Assert.Equal(206, report.ErrorCount);
        Assert.Equal(2, report.ErrorFileCount);
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
    public async Task Added_file_with_an_error_is_reported()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Write("Lib/Extra.cs", "namespace Lib;\n\npublic static class Extra\n{\n    public static int Value() => missing;\n}\n");
        var report = await engine.CheckAsync("Lib/Extra.cs");
        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("Lib/Extra.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
    }

    [Fact]
    public async Task Added_file_used_by_another_edit_is_visible()
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

        // The reach queries run after a real check, so the baseline is loaded the way the check loads it. The check names
        // both edits, as a hook does, so neither waits for its watcher event to reach the current view.
        await engine.CheckAsync("App/Report.cs", "Lib/Calc.cs");
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

    [Fact]
    public async Task Errors_head_has_in_a_file_renamed_since_are_not_reported_under_the_new_name()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        engine.Repo.Commit("commit a broken file");

        // A rename that git status reports as a deleted file and a new one, as a file manager or an agent's mv makes it.
        engine.Repo.Write("Lib/Arithmetic.cs", engine.Repo.Read("Lib/Calc.cs"));
        engine.Repo.Delete("Lib/Calc.cs");
        Assert.Empty((await engine.CheckAsync("Lib/Arithmetic.cs")).Errors);
        Assert.Empty((await engine.CheckAllAsync()).Errors);

        engine.Repo.Replace("Lib/Arithmetic.cs", "a + b;", "a + anotherError;");
        var error = Assert.Single((await engine.CheckAsync("Lib/Arithmetic.cs")).Errors).Error;
        Assert.Contains("anotherError", error.Message, StringComparison.Ordinal);
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
    public async Task A_receiver_change_in_a_later_extension_block_breaks_its_callers()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Ext.cs", "namespace Lib;\n\npublic static class Ext\n{\n    extension(string s)\n    {\n        public int Len() => s.Length;\n    }\n\n    extension(int i)\n    {\n        public int Twice() => i.GetHashCode() * 2;\n    }\n}\n"),
            ("App/UseExt.cs", "using Lib;\n\nnamespace App;\n\npublic static class UseExt\n{\n    public static int Run() => \"ab\".Len() + 3.Twice();\n}\n"));
        engine.Repo.Replace("Lib/Ext.cs", "extension(int i)", "extension(string i)");
        var report = await engine.CheckAsync("Lib/Ext.cs");
        Assert.Contains(report.Errors, e => e.Error.Path == "App/UseExt.cs");
    }

    [Fact]
    public async Task A_changed_member_in_a_later_extension_block_breaks_its_callers()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Ext.cs", "namespace Lib;\n\npublic static class Ext\n{\n    extension(string s)\n    {\n        public int Size() => s.Length;\n    }\n\n    extension(int i)\n    {\n        public int Size() => i;\n    }\n}\n"),
            ("App/UseExt.cs", "using Lib;\n\nnamespace App;\n\npublic static class UseExt\n{\n    public static int Run() => 3.Size();\n}\n"));
        engine.Repo.Replace("Lib/Ext.cs", "public int Size() => i;", "public int Size(int scale) => i * scale;");
        var report = await engine.CheckAsync("Lib/Ext.cs");
        var error = Assert.Single(report.Errors);
        Assert.Equal("App/UseExt.cs", error.Error.Path);
        Assert.Equal(new Cause.Removed("public int Size()"), error.Cause);
    }

    [Fact]
    public async Task A_base_type_removed_from_a_later_part_of_a_partial_class_breaks_its_users()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Part.cs", "namespace Lib;\n\npublic partial class Part\n{\n}\n\npublic partial class Part : System.IDisposable\n{\n    public void Dispose()\n    {\n    }\n}\n"),
            ("App/UsePart.cs", "namespace App;\n\npublic static class UsePart\n{\n    public static void Run()\n    {\n        using var part = new Lib.Part();\n    }\n}\n"));
        engine.Repo.Replace("Lib/Part.cs", "public partial class Part : System.IDisposable", "public partial class Part");
        var report = await engine.CheckAsync("Lib/Part.cs");
        Assert.Equal("CS1674", Assert.Single(report.Errors, e => e.Error.Path == "App/UsePart.cs").Error.Id);
    }

    [Fact]
    public async Task An_indexer_made_internal_beside_an_explicit_one_breaks_its_callers()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Bag.cs", "namespace Lib;\n\npublic interface IBag\n{\n    int this[int i] { get; }\n}\n\npublic class Bag : IBag\n{\n    int IBag.this[int i] => i;\n\n    public int this[int i] => i + 1;\n}\n"),
            ("App/UseBag.cs", "namespace App;\n\npublic static class UseBag\n{\n    public static int Run() => new Lib.Bag()[0];\n}\n"));
        engine.Repo.Replace("Lib/Bag.cs", "public int this[int i]", "internal int this[int i]");
        var report = await engine.CheckAsync("Lib/Bag.cs");
        Assert.Contains(report.Errors, e => e.Error.Path == "App/UseBag.cs");
    }

    [Fact]
    public async Task An_implicit_conversion_made_explicit_breaks_the_files_that_name_its_type()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Result.cs", "namespace Lib;\n\npublic sealed class Failure\n{\n}\n\npublic readonly struct Result<T>\n{\n    public static implicit operator Result<T>(T value) => default;\n\n    public static implicit operator Result<T>(Failure failure) => default;\n}\n"),
            ("App/UseResult.cs", "namespace App;\n\npublic static class UseResult\n{\n    public static Lib.Result<int> Run() => 5;\n}\n"));
        engine.Repo.Replace("Lib/Result.cs", "public static implicit operator Result<T>(T value)", "public static explicit operator Result<T>(T value)");
        var report = await engine.CheckAsync("Lib/Result.cs");
        // A reference search does not return the places an implicit conversion is applied, so the type's name reaches them.
        var error = Assert.Single(report.Errors, e => e.Error.Path == "App/UseResult.cs");
        Assert.Equal("CS0266", error.Error.Id);
        Assert.Equal(new Cause.Removed("public static implicit operator Result<T>(T value)"), error.Cause);
    }

    [Fact]
    public async Task A_signature_change_inside_an_if_DEBUG_region_breaks_its_callers()
    {
        // The engine evaluates the Debug configuration, so DEBUG is defined and the region is compiled.
        await using var engine = await StartWithAsync(
            ("Lib/Trace.cs", "namespace Lib;\n\npublic static class Trace\n{\n#if DEBUG\n    public static int Write(int value) => value;\n#endif\n}\n"),
            ("App/UseTrace.cs", "namespace App;\n\npublic static class UseTrace\n{\n    public static int Run() => Lib.Trace.Write(1);\n}\n"));
        engine.Repo.Replace("Lib/Trace.cs", "Write(int value) => value;", "Write(string value) => value.Length;");
        var report = await engine.CheckAsync("Lib/Trace.cs");
        Assert.Equal("CS1503", Assert.Single(report.Errors, e => e.Error.Path == "App/UseTrace.cs").Error.Id);
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
    }

    [Fact]
    public async Task A_member_removed_inside_one_target_framework_s_region_breaks_its_callers()
    {
        // Multi builds for net8.0 and net10.0, and only net10.0 defines NET10_0_OR_GREATER.
        await using var engine = await StartWithAsync(
            ("Multi/Shape.cs", "namespace Multi;\n\npublic static class Shape\n{\n    public static int Sides(int count) => count;\n\n#if NET10_0_OR_GREATER\n    public static int Area(int side) => side * side;\n#endif\n}\n"),
            ("Multi/UseShape.cs", "namespace Multi;\n\npublic static class UseShape\n{\n#if NET10_0_OR_GREATER\n    public static int Run() => Shape.Area(2);\n#endif\n}\n"));
        engine.Repo.Replace("Multi/Shape.cs", "#if NET10_0_OR_GREATER\n    public static int Area(int side) => side * side;\n#endif\n", "");
        var report = await engine.CheckAsync("Multi/Shape.cs");
        var error = Assert.Single(report.Errors);
        Assert.Equal("Multi/UseShape.cs", error.Error.Path);
        Assert.Equal(new Cause.Removed("public static int Area(int side)"), error.Cause);
    }

    [Fact]
    public async Task A_rename_beside_a_member_in_an_active_region_names_the_renamed_member()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Calc.cs", "namespace Lib;\n\npublic class Calc\n{\n#if DEBUG\n    public int Mul(int a, int b) => a * b;\n#endif\n\n    public int Add(int a, int b) => a + b;\n}\n"));
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var report = await engine.CheckAsync("Lib/Calc.cs");

        // The region is active in both versions, so Mul did not change and only the rename can be the cause.
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], report.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
        Assert.All(report.Errors, e => Assert.Equal(new Cause.Removed("public int Add(int a, int b)"), e.Cause));
    }

    [Fact]
    public async Task A_file_two_changes_reach_gets_the_cause_of_the_change_in_the_first_path()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // App/Program.cs calls both Calc.Add and IGreeter.Greet, so both changes reach it.
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        var addRemoved = new Cause.Removed("public int Add(int a, int b)");

        // A file has one cause, so both of Program.cs's errors (the missing argument and the missing Add) name it.
        var report = await engine.CheckAllAsync();
        var inProgram = report.Errors.Where(e => e.Error.Path == "App/Program.cs").ToList();
        Assert.Equal(2, inProgram.Count);
        Assert.All(inProgram, e => Assert.Equal(addRemoved, e.Cause));

        // The order the targets arrive in does not decide it: Lib/Calc.cs comes before Lib/Greeting.cs by path.
        var precise = Assert.IsType<Reach.Precise>(await engine.ReachAsync("Lib/Greeting.cs", "Lib/Calc.cs"));
        Assert.Equal(addRemoved, precise.Causes[engine.Repo.PathOf("App/Program.cs")]);
    }

    [Fact]
    public async Task A_constructor_added_to_a_class_with_only_the_implicit_one_breaks_every_construction()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // App/Program.cs calls new Lib.Calc() and CalcTests.cs the target-typed new(); both bound the implicit constructor.
        engine.Repo.Replace("Lib/Calc.cs", "public class Calc\n{\n", "public class Calc\n{\n    public Calc(int seed)\n    {\n    }\n\n");
        var report = await engine.CheckAsync("Lib/Calc.cs");
        Assert.Equal(["App/Program.cs", "Lib.Tests/CalcTests.cs"], report.Errors.Select(e => e.Error.Path).Order(StringComparer.Ordinal));
        Assert.All(report.Errors, e => Assert.Equal("CS7036", e.Error.Id));
        Assert.All(report.Errors, e => Assert.Equal(new Cause.Changed("public Calc(int seed)"), e.Cause));
    }

    [Fact]
    public async Task A_constructor_added_to_a_base_class_breaks_the_derived_class()
    {
        await using var engine = await StartWithAsync(
            ("Lib/Shape.cs", "namespace Lib;\n\npublic class Shape\n{\n}\n"),
            ("App/Square.cs", "namespace App;\n\npublic sealed class Square : Lib.Shape\n{\n}\n"));
        engine.Repo.Replace("Lib/Shape.cs", "public class Shape\n{\n", "public class Shape\n{\n    public Shape(int sides)\n    {\n    }\n");
        var report = await engine.CheckAsync("Lib/Shape.cs");
        Assert.Equal("CS7036", Assert.Single(report.Errors, e => e.Error.Path == "App/Square.cs").Error.Id);
    }

    [Fact]
    public async Task Past_500_candidates_whole_projects_are_checked_and_each_error_and_file_counts_once()
    {
        // Strict holds Thing.cs and 510 more files, so a broad change in it reaches more than 500 candidates.
        await using var engine = await StartWithAsync(
            [.. Enumerable.Range(0, 510).Select(i => ($"Strict/Generated/Part{i}.cs", $"namespace Strict;\n\ninternal static class Part{i}\n{{\n}}\n"))]);
        // A changed type header is broad, and CA1825 is an analyzer error in this project.
        engine.Repo.Replace("Strict/Thing.cs", "public static class Thing", "public static partial class Thing");
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + 1;");
        var report = await engine.CheckAsync("Strict/Thing.cs");

        Assert.True(report.CheckedWholeProjects);
        var error = Assert.Single(report.Errors);
        Assert.Equal("CA1825", error.Error.Id);
        Assert.True(error.Error.FromAnalyzer);
        Assert.Null(error.Cause);
        // Each source file once; the files the build generates under obj are compiled but not counted.
        Assert.Equal(511, report.FilesChecked);
    }

    /// <summary>The standard fixture with <paramref name="files"/> written and committed, so they are part of HEAD.</summary>
    private static async Task<InProcessEngine> StartWithAsync(params (string Path, string Content)[] files)
    {
        var repo = FixtureRepo.CreateStandard();
        foreach (var (path, content) in files)
            repo.Write(path, content);
        repo.Commit("fixture files");
        return await InProcessEngine.StartAsync(repo);
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
