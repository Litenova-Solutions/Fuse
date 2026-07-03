using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// N4: fuse doctor reports the achieved load tier and the concrete per-project downgrade reason. A workspace with
// no solution or project loads at the syntax tier, which the diagnosis names rather than failing opaquely.
public sealed class DoctorDiagnoseTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-doctor-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        // Source files but no .csproj or .sln: the loader has nothing to open, so the tier is syntax.
        File.WriteAllText(Path.Combine(_root, "A.cs"), "namespace Demo; public class Alpha { }");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Diagnose_reports_syntax_tier_when_no_project_is_present()
    {
        var diagnosis = await CreateIndexer().DiagnoseLoadAsync(_root, CancellationToken.None);

        Assert.Equal("syntax", diagnosis.Tier);
        Assert.Equal(0, diagnosis.ProjectsTotal);
        Assert.Equal(0, diagnosis.ProjectsLoaded);
        Assert.Empty(diagnosis.Projects);
        // The syntax-only case names its reason rather than failing opaquely.
        Assert.Contains(diagnosis.Diagnostics, d => d.Code == "syntax-only");
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline, new FileHashService()),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            SemanticAnalysisRunner.CreateDefault());
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }
}
