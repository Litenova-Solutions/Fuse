using Fuse.Check;
using Fuse.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Tests.Engine;

/// <summary>
///     What a sync under the writer lock never does: emit a summary, flip a project from source to the
///     summary the background kept for it, or maintain a session view when no session view is kept. A body
///     edit with summaries supplied keeps the same summary reference, so no dependent binds again for it,
///     and the reuse key computes an unchanged project's fingerprint once for its state.
/// </summary>
public class WriterLockTests
{
    [Fact]
    public async Task A_sync_never_emits_a_summary()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("App/Report.cs");
        Assert.False(SummariesStored(engine.Repo));

        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        await engine.SyncAsync("App/Report.cs");
        Assert.False(SummariesStored(engine.Repo));

        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);
        Assert.False(SummariesStored(engine.Repo));
    }

    [Fact]
    public async Task A_check_of_a_body_edit_with_summaries_cached_performs_no_reference_flip()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("App/Report.cs");
        var cancellationToken = TestContext.Current.CancellationToken;
        var libNode = engine.Workspace.Graph.Projects.Single(p => p.Name == "Lib");
        Assert.True(await engine.Workspace.EmitSummaryAsync(libNode, cancellationToken));
        await engine.Workspace.RebuildViewsAsync(cancellationToken);

        var before = engine.Workspace.Current.Projects.Single(p => p.Name == "App");
        var lib = engine.Workspace.Current.Projects.Single(p => p.Name == "Lib");
        Assert.DoesNotContain(before.ProjectReferences, reference => reference.ProjectId == lib.Id);
        var summaryBefore = Assert.Single(before.MetadataReferences.OfType<PortableExecutableReference>(), reference => reference.FilePath is not null && reference.FilePath.Contains("summaries", StringComparison.Ordinal));

        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        var report = await engine.CheckAsync("App/Report.cs");

        Assert.Empty(report.Errors);
        var after = engine.Workspace.Current.Projects.Single(p => p.Name == "App");
        Assert.Equal(before.ProjectReferences, after.ProjectReferences);
        var summaryAfter = Assert.Single(after.MetadataReferences.OfType<PortableExecutableReference>(), reference => reference.FilePath is not null && reference.FilePath.Contains("summaries", StringComparison.Ordinal));
        Assert.Same(summaryBefore, summaryAfter);
    }

    [Fact]
    public async Task A_sync_maintains_no_session_view_when_no_other_live_session_wrote_a_file()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsSessionAsync("A", "Lib/Calc.cs");
        Assert.Equal(0, engine.Workspace.SessionViewCount);

        engine.Repo.Replace("Lib/Calc.cs", "a + b;", "a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");

        Assert.Equal(0, engine.Workspace.SessionViewCount);
    }

    [Fact]
    public async Task The_reuse_key_of_an_unchanged_project_is_computed_once_for_its_state()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var cancellationToken = TestContext.Current.CancellationToken;
        var solution = TwoProjects(repo);
        var collector = new DiagnosticCollector(repo.Root, () => 0, _ => { });
        collector.Begin(AnalyzerRun.Full, reuse: true);
        var first = repo.Root.PathOf(repo.Full("C1.cs"));

        Assert.Empty(await collector.ForFileAsync(solution, first, cancellationToken));
        Assert.Equal(2, collector.DeclarationFingerprintComputations);

        // A body edit in the second project moves only that project's state: the first project's
        // fingerprint is read back, and the second is computed once more, however often a file is keyed.
        var second = solution.Projects.Single(p => p.Name == "P2").Documents.Single();
        var edited = solution.WithDocumentText(second.Id, SourceText.From("public class C2 { public int M() => 2; }\n"));
        Assert.Empty(await collector.ForFileAsync(edited, first, cancellationToken));
        Assert.Equal(3, collector.DeclarationFingerprintComputations);
        Assert.Empty(await collector.ForFileAsync(edited, first, cancellationToken));
        Assert.Equal(3, collector.DeclarationFingerprintComputations);
    }

    /// <summary>True when the shared state directory of <paramref name="repo"/> holds a summary assembly.</summary>
    private static bool SummariesStored(FixtureRepo repo)
    {
        var directory = Path.Combine(repo.Root.SharedStateDirectory, "summaries");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();
    }

    /// <summary>Two one-file projects in one adhoc solution over files <paramref name="repo"/> holds.</summary>
    private static Solution TwoProjects(FixtureRepo repo)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var (name, file, source) in new[]
        {
            ("P1", "C1.cs", "public class C1 { public int M() => 1; }\n"),
            ("P2", "C2.cs", "public class C2 { public int M() => 1; }\n"),
        })
        {
            var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, name, LanguageNames.CSharp, filePath: repo.Full(name + ".csproj"))
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithParseOptions(new CSharpParseOptions())
                .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            solution = solution.AddProject(info);
            solution = solution.AddDocument(DocumentId.CreateNewId(info.Id), file, SourceText.From(source), filePath: repo.Full(file));
        }

        return solution;
    }
}
