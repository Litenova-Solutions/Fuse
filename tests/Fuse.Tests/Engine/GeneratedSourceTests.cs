using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A file a build generates into <c>obj</c>, such as an API client written from another project's output, is read as
///     the last build wrote it. An error about something it declares names it, and once a build writes it again the next
///     check reads the new file, although the file watcher skips <c>obj</c>.
/// </summary>
public class GeneratedSourceTests
{
    private const string OldClient = "namespace Lib;\n\npublic class Client\n{\n    public int Old() => 1;\n}\n";
    private const string NewClient = "namespace Lib;\n\npublic class Client\n{\n    public int Old() => 1;\n\n    public int Fresh() => 2;\n}\n";

    [Fact]
    public async Task An_error_about_a_generated_type_names_its_file_and_a_build_that_writes_it_again_clears_it()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Lib.csproj", "</Project>", "<ItemGroup><Compile Include=\"obj/Gen/Client.cs\" /></ItemGroup></Project>");
        repo.Write("Lib/obj/Gen/Client.cs", OldClient);
        repo.Commit();
        await using var engine = await InProcessEngine.StartAsync(repo);

        // The agent calls the member its change to the generator's source adds, before a build writes the client again.
        repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Format(new Lib.Client().Fresh())");
        var stale = await engine.CheckAsync("App/Report.cs");
        var error = Assert.Single(stale.Errors);
        Assert.Equal("CS1061", error.Error.Id);
        Assert.Equal("Lib/obj/Gen/Client.cs", error.GeneratedFile);

        repo.Write("Lib/obj/Gen/Client.cs", NewClient);
        var fresh = await engine.CheckAsync("App/Report.cs");
        Assert.Empty(fresh.Errors);

        // A project that loads later derives both views again; the client the build wrote stays in them.
        repo.Replace("Lib.Tests/CalcTests.cs", "namespace Lib.Tests;", "namespace Lib.Tests;\n\n// loads Lib.Tests");
        await engine.CheckAsync("Lib.Tests/CalcTests.cs");
        var afterLoad = await engine.CheckAsync("App/Report.cs");
        Assert.Empty(afterLoad.Errors);
    }

    [Fact]
    public async Task An_error_about_a_type_in_the_repository_names_no_generated_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "Lib.Formatter.Format(value)", "Lib.Formatter.Missing(value)");

        var result = await engine.CheckAsync("App/Report.cs");

        var error = Assert.Single(result.Errors);
        Assert.Equal("CS0117", error.Error.Id);
        Assert.Null(error.GeneratedFile);
    }
}
