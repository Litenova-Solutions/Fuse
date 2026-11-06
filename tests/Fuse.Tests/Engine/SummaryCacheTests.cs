using Fuse.Check;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Tests.Engine;

/// <summary>
///     A project's summary is an image of its HEAD compilation, private members included, kept in a
///     content-addressed cache under the shared directory, and supplied only for references to projects outside the
///     change cone. A missing, unreadable or unkeyable summary, and a HEAD compilation with errors, fall back to
///     source, and the views bind cone projects from source whether a summary is kept or not.
/// </summary>
public class SummaryCacheTests
{
    [Fact]
    public async Task A_summary_is_emitted_from_HEAD_with_private_members_and_then_read_from_the_cache()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = ProjectWith(repo, "public class C { private int Secret() => 1; internal int Inside() => 2; public int Open() => 3; }\n");
        var cache = new SummaryCache(repo.Root, _ => { });
        var cancellationToken = TestContext.Current.CancellationToken;

        var reference = await cache.GetOrEmitAsync(project, "test-key", cancellationToken);

        Assert.NotNull(reference);
        var file = Assert.IsType<PortableExecutableReference>(reference, exactMatch: false).FilePath;
        Assert.NotNull(file);
        Assert.Contains("summaries", file, StringComparison.Ordinal);
        Assert.True(File.Exists(file));
        Assert.Same(reference, await cache.GetOrEmitAsync(project, "test-key", cancellationToken));
        Assert.NotNull(new SummaryCache(repo.Root, _ => { }).TryGet("test-key", project));

        // The consumer imports every member, as a view that supplies a summary does, so the members the
        // summary carries below the public surface are among the type's members.
        var consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText("public class Use { public C Make() => new C(); }\n", cancellationToken: TestContext.Current.CancellationToken)],
            [reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
        var type = consumer.GetTypeByMetadataName("C");
        Assert.NotNull(type);
        Assert.Empty(type.DeclaringSyntaxReferences);
        Assert.Equal(Accessibility.Private, Assert.Single(type.GetMembers("Secret")).DeclaredAccessibility);
        Assert.Equal(Accessibility.Internal, Assert.Single(type.GetMembers("Inside")).DeclaredAccessibility);
    }

    [Fact]
    public async Task A_HEAD_compilation_with_errors_and_an_unreadable_image_fall_back_to_source()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new SummaryCache(repo.Root, _ => { });

        var broken = ProjectWith(repo, "public class C { public int M() => missing; }\n");
        Assert.Null(await cache.GetOrEmitAsync(broken, "broken-key", cancellationToken));
        Assert.Null(cache.TryGet("no-such-key", broken));

        var sound = ProjectWith(repo, "public class C { public int M() => 1; }\n");
        var reference = await cache.GetOrEmitAsync(sound, "sound-key", cancellationToken);
        Assert.NotNull(reference);
        var file = Assert.IsType<PortableExecutableReference>(reference, exactMatch: false).FilePath!;
        File.WriteAllText(file, "not an image");

        Assert.Null(new SummaryCache(repo.Root, _ => { }).TryGet("sound-key", sound));
        Assert.NotNull(await new SummaryCache(repo.Root, _ => { }).GetOrEmitAsync(sound, "sound-key", cancellationToken));
    }

    [Fact]
    public async Task The_summary_key_covers_the_HEAD_sources_and_the_store_supplies_what_it_keeps()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.CheckAsync("Lib/Calc.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        var generation = engine.Workspace.PinGeneration();
        var lib = generation.Baseline.Projects.Single(p => p.Name == "Lib");
        var cache = new SummaryCache(engine.Repo.Root, _ => { });

        var key = await cache.KeyForAsync(generation.Head, lib, generation.Graph, cancellationToken);
        Assert.NotNull(key);
        Assert.Null(await cache.KeyForAsync(null, lib, generation.Graph, cancellationToken));

        var document = lib.Documents.Single(d => d.FilePath!.EndsWith("Calc.cs", StringComparison.Ordinal));
        var changed = lib.Solution.WithDocumentText(document.Id, SourceText.From("namespace Lib;\npublic class Calc { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; public int Extra() => 1; }\n"));
        var changedLib = changed.Projects.Single(p => p.Name == "Lib");
        Assert.NotEqual(key, await cache.KeyForAsync(generation.Head, changedLib, generation.Graph, cancellationToken));

        var store = new BaselineStore(engine.Repo.Root, _ => { });
        Assert.NotNull(await store.SummaryForAsync(generation.Head, lib, generation.Graph, cancellationToken));
        Assert.NotNull(await store.StoredSummaryForAsync(generation.Head, lib, generation.Graph, cancellationToken));
        Assert.NotNull(new SummaryCache(engine.Repo.Root, _ => { }).TryGet(key!, lib));
    }

    [Fact]
    public async Task Views_supply_a_project_outside_the_cone_by_its_summary_and_keep_its_source()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await PrimeSummariesAsync(engine);
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");

        var report = await engine.CheckAsync("App/Report.cs");

        Assert.Empty(report.Errors);
        foreach (var view in new[] { engine.Workspace.Current, engine.Workspace.Baseline })
        {
            var app = view.Projects.Single(p => p.Name == "App");
            var lib = view.Projects.Single(p => p.Name == "Lib");
            Assert.DoesNotContain(app.ProjectReferences, r => r.ProjectId == lib.Id);
            var summary = Assert.Single(app.MetadataReferences.OfType<PortableExecutableReference>(), r => r.FilePath is not null && r.FilePath.Contains("summaries", StringComparison.Ordinal));
            Assert.True(File.Exists(summary.FilePath));
            Assert.NotEmpty(view.GetDocumentIdsWithFilePath(engine.Repo.Full("Lib/Calc.cs")));
        }
    }

    [Fact]
    public async Task Views_bind_from_source_everywhere_when_summaries_are_not_used()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Workspace.UseSummaries = false;
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");

        var report = await engine.CheckAsync("App/Report.cs");

        Assert.Empty(report.Errors);
        var app = engine.Workspace.Current.Projects.Single(p => p.Name == "App");
        var lib = engine.Workspace.Current.Projects.Single(p => p.Name == "Lib");
        Assert.Contains(app.ProjectReferences, r => r.ProjectId == lib.Id);
        Assert.False(Directory.Exists(Path.Combine(engine.Repo.Root.SharedStateDirectory, "summaries")));
    }

    [Fact]
    public async Task An_error_against_a_summary_is_reported_as_it_is_against_source()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await PrimeSummariesAsync(engine);
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(\"x\", 2)");

        var report = await engine.CheckAsync("App/Program.cs");

        Assert.Equal("CS1503", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task The_clear_calls_shed_session_views_HEAD_compilations_and_idle_cone_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        await engine.CheckAsync("App/Report.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new[] { engine.Repo.PathOf("App/Report.cs") };

        var first = await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken);
        Assert.Same(first, await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken));
        await engine.Workspace.ClearSessionViewsAsync(cancellationToken);
        Assert.NotSame(first, await engine.Workspace.CurrentWithHeadContentAsync(paths, cancellationToken));

        await engine.Workspace.ClearHeadCompilationsAsync(cancellationToken);
        Assert.Empty(engine.Workspace.Baseline.Projects);
        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);
        Assert.NotEmpty(engine.Workspace.Baseline.Projects);

        var tests = engine.Workspace.Graph.Projects.Single(p => p.Name == "App.Tests");
        await engine.Workspace.EnsureLoadedAsync([tests], cancellationToken);
        Assert.Contains(engine.Workspace.Current.Projects, p => p.Name == "App.Tests");
        await engine.Workspace.ClearIdleConeProjectsAsync(cancellationToken);
        Assert.DoesNotContain(engine.Workspace.Current.Projects, p => p.Name == "App.Tests");
        Assert.Contains(engine.Workspace.Current.Projects, p => p.Name == "App");
        Assert.Contains(engine.Workspace.Current.Projects, p => p.Name == "Lib");
        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);
    }

    /// <summary>
    ///     Loads the fixture projects with a first check, emits every loaded project's summary the way the
    ///     background warm does, and derives the views from the loader again, which is the moment a kept
    ///     summary starts being supplied. No check or sync in this helper emits a summary.
    /// </summary>
    private static async Task PrimeSummariesAsync(InProcessEngine engine)
    {
        await engine.CheckAsync("App/Report.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var node in engine.Workspace.Graph.Projects.ToList())
            await engine.Workspace.EmitSummaryAsync(node, cancellationToken);
        await engine.Workspace.RebuildViewsAsync(cancellationToken);
    }

    /// <summary>One project over <paramref name="source"/>, in an adhoc solution whose only file the repository holds.</summary>
    private static Project ProjectWith(FixtureRepo repo, string source)
    {
        // The compilation and parse options a loaded project always carries, named here so the project's
        // compilation is a library's, as it is for every project the loader opens.
        var project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "P", "P", LanguageNames.CSharp, filePath: repo.Full("P.csproj"))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions())
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(project);
        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "C.cs", SourceText.From(source), filePath: repo.Full("C.cs"));
        return solution.Projects.Single();
    }
}
