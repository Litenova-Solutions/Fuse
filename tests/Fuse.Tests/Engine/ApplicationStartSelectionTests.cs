using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A change that reaches an application runs the test classes that start it whole and selects the rest by name: a
///     direct call in an integration project is narrowed to its test, while a class the walk cannot tell, a project
///     without classes, and a test application reached in itself keep the project whole.
/// </summary>
public class ApplicationStartSelectionTests
{
    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task Direct_call_in_an_integration_project_is_narrowed_to_its_test()
    {
        await using var engine = await InProcessEngine.StartAsync(WithApplicationStartups());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        var selection = await SelectAsync(engine, "App/Report.cs");

        Assert.Single(selection);
        var app = Assert.IsType<TestSelection.Methods>(selection["App.Tests"]);
        Assert.Equal(3, app.Patterns.Count);
        Assert.Contains("App.Tests.FactoryTests.", app.Patterns);
        Assert.Contains("App.Tests.ProcessTests.", app.Patterns);
        Assert.Contains("App.Tests.ReportTests.Formats", app.Patterns);
        Assert.Contains(engine.Log, line => line == "test selection: App.Tests: 2 of 3 test class(es) start App; selecting them whole, the rest by name");
    }

    [Fact]
    public async Task Doubtful_class_keeps_the_project_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithDoubtfulStartup());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        var selection = await SelectAsync(engine, "App/Report.cs");

        Assert.Single(selection);
        var app = Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
        Assert.Equal("App uses the changed code and runs behind an application host", app.Reason);
        Assert.Contains(engine.Log, line => line == "test selection: App.Tests: 1 of 4 test class(es) may start App; running the project whole");
    }

    [Fact]
    public async Task Test_project_without_classes_runs_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithClasslessTests());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        var selection = await SelectAsync(engine, "App/Report.cs");

        Assert.Single(selection);
        var app = Assert.IsType<TestSelection.Whole>(selection["App.Tests"]);
        Assert.Equal("App uses the changed code and runs behind an application host", app.Reason);
        Assert.Contains(engine.Log, line => line == "test selection: App.Tests: no test class found; running the project whole");
    }

    [Fact]
    public async Task Framework_invoked_code_in_a_test_application_runs_the_project_whole()
    {
        await using var engine = await InProcessEngine.StartAsync(WithPlatformHelper());
        engine.Repo.Replace("Tests/Helper.cs", "=> \"helper\";", "=> \"helper!\";");
        var selection = await SelectAsync(engine, "Tests/Helper.cs");

        Assert.Single(selection);
        var tests = Assert.IsType<TestSelection.Whole>(selection["Tests"]);
        Assert.StartsWith("ToString", tests.Reason, StringComparison.Ordinal);
        Assert.EndsWith("is called by an application host or a framework", tests.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The standard repository whose entry point calls <c>Report.Line</c>, with a factory-based and a
    ///     process-starting test class committed in <c>App.Tests</c> beside the direct call in <c>ReportTests</c>.
    /// </summary>
    private static FixtureRepo WithApplicationStartups()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(1, 2) + App.Report.Line(1).Length");
        repo.Write("App/Program.cs", repo.Read("App/Program.cs") + "\npublic partial class Program\n{\n}\n");
        repo.Write("App.Tests/FactoryTests.cs", """
            using Microsoft.AspNetCore.Mvc.Testing;

            namespace Microsoft.AspNetCore.Mvc.Testing
            {
                public class WebApplicationFactory<TEntryPoint>
                    where TEntryPoint : class
                {
                }
            }

            namespace App.Tests
            {
                public class FactoryTests
                {
                    private readonly WebApplicationFactory<Program> _factory = null!;

                    [Fact]
                    public void Boots()
                    {
                    }
                }
            }
            """);
        repo.Write("App.Tests/ProcessTests.cs", """
            namespace App.Tests;

            public class ProcessTests
            {
                [Fact]
                public void Runs() => System.Diagnostics.Process.Start("App.dll");
            }
            """);
        repo.Commit("add integration test classes to App.Tests");
        return repo;
    }

    /// <summary>The startup repository with a committed test class that loads an assembly by name.</summary>
    private static FixtureRepo WithDoubtfulStartup()
    {
        var repo = WithApplicationStartups();
        repo.Write("App.Tests/ReflectionTests.cs", """
            namespace App.Tests;

            public class ReflectionTests
            {
                [Fact]
                public void Loads() => System.Reflection.Assembly.Load("App");
            }
            """);
        repo.Commit("add a reflection test class to App.Tests");
        return repo;
    }

    /// <summary>The standard repository whose entry point calls <c>Report.Line</c>, with the test class replaced by top-level statements.</summary>
    private static FixtureRepo WithClasslessTests()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(1, 2) + App.Report.Line(1).Length");
        repo.Write("App.Tests/ReportTests.cs", """
            System.Console.WriteLine(App.Report.Line(5));
            """);
        repo.Commit("replace the test class with top-level statements");
        return repo;
    }

    /// <summary>An xUnit v3 platform repository with a committed helper class that overrides <c>ToString</c>.</summary>
    private static FixtureRepo WithPlatformHelper()
    {
        var files = new Dictionary<string, string>(FixtureTemplate.PlatformRepo("xunit-v3", optIn: false));
        files["Tests/Helper.cs"] = """
            namespace Platform.Tests;

            public class Helper
            {
                public override string ToString() => "helper";
            }
            """;
        var repo = FixtureRepo.CreateEmpty(files);
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q");
        return repo;
    }
}
