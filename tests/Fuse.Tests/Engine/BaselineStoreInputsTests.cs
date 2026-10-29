using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Tests.Engine;

/// <summary>The baseline key covers what analyzer results depend on: an additional file's content and the global options.</summary>
public class BaselineStoreInputsTests
{
    [Fact]
    public async Task Editing_an_additional_file_moves_the_key()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.CheckAsync("Lib/Calc.cs");
        var baseline = engine.Workspace.Baseline;
        var graph = engine.Workspace.Graph;
        var head = FixtureRepo.Run(engine.Repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
        var lib = baseline.GetProject(baseline.GetDocumentIdsWithFilePath(engine.Repo.Full("Lib/Calc.cs")).Single().ProjectId)!;
        engine.Repo.Write("Lib/Data.txt", "version one\n");
        var withData = lib.AddAdditionalDocument("Data.txt", SourceText.From("version one\n"), filePath: engine.Repo.Full("Lib/Data.txt"));

        var plain = BaselineStore.KeyFor(head, baseline, graph, engine.Repo.Root);
        var keyed = BaselineStore.KeyFor(head, withData.Project.Solution, graph, engine.Repo.Root);

        Assert.NotNull(plain);
        Assert.NotNull(keyed);
        Assert.NotEqual(plain, keyed);

        engine.Repo.Write("Lib/Data.txt", "version two\n");
        Assert.NotEqual(keyed, BaselineStore.KeyFor(head, withData.Project.Solution, graph, engine.Repo.Root));
    }

    [Fact]
    public async Task Changing_a_global_option_moves_the_key()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.CheckAsync("Lib/Calc.cs");
        var baseline = engine.Workspace.Baseline;
        var graph = engine.Workspace.Graph;
        var head = FixtureRepo.Run(engine.Repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
        var lib = baseline.GetProject(baseline.GetDocumentIdsWithFilePath(engine.Repo.Full("Lib/Calc.cs")).Single().ProjectId)!;
        var withOptions = lib.AddAnalyzerConfigDocument(
            "Test.globalconfig",
            SourceText.From("is_global = true\ndotnet_code_quality_unused_parameters = all\n"),
            filePath: engine.Repo.Full("Lib/Test.globalconfig"));

        var plain = BaselineStore.KeyFor(head, baseline, graph, engine.Repo.Root);
        var keyed = BaselineStore.KeyFor(head, withOptions.Project.Solution, graph, engine.Repo.Root);

        Assert.NotNull(plain);
        Assert.NotNull(keyed);
        Assert.NotEqual(plain, keyed);
    }
}
