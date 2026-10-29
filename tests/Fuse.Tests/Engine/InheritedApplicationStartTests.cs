using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A test class that only inherits its tests is still enumerated, so a change that reaches the application selects
///     it when it starts the application. Without the base walk it would be missed and its inherited tests never selected.
/// </summary>
public class InheritedApplicationStartTests
{
    [Fact]
    public async Task Derived_class_with_no_own_tests_is_selected_when_it_starts_the_application()
    {
        await using var engine = await InProcessEngine.StartAsync(WithInheritedStartup());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        await engine.Workspace.SyncAsync([engine.Repo.PathOf("App/Report.cs")], TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync([engine.Repo.PathOf("App/Report.cs")], TestContext.Current.CancellationToken);
        var selection = result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);

        Assert.Single(selection);
        var app = Assert.IsType<TestSelection.Methods>(selection["App.Tests"]);
        Assert.Contains("App.Tests.BaseTests.", app.Patterns);
        Assert.Contains("App.Tests.DerivedTests.", app.Patterns);
        Assert.Contains("App.Tests.ReportTests.Formats", app.Patterns);
        Assert.Contains(engine.Log, line => line == "test selection: App.Tests: 2 of 3 test class(es) start App; selecting them whole, the rest by name");
    }

    /// <summary>
    ///     The standard repository whose entry point calls <c>Report.Line</c>, with a factory-based base test class and a
    ///     derived class that declares no test committed in <c>App.Tests</c> beside the direct call in <c>ReportTests</c>.
    /// </summary>
    private static FixtureRepo WithInheritedStartup()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(1, 2) + App.Report.Line(1).Length");
        repo.Write("App/Program.cs", repo.Read("App/Program.cs") + "\npublic partial class Program\n{\n}\n");
        repo.Write("App.Tests/BaseTests.cs", """
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
                public class BaseTests
                {
                    private readonly WebApplicationFactory<Program> _factory = null!;

                    [Fact]
                    public void Boots()
                    {
                    }
                }
            }
            """);
        repo.Write("App.Tests/DerivedTests.cs", """
            namespace App.Tests;

            public class DerivedTests : BaseTests
            {
            }
            """);
        repo.Commit("add inherited test classes to App.Tests");
        return repo;
    }
}
