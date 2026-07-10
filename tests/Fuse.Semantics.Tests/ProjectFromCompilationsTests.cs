using System.Collections.Immutable;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// S1 step 4: projecting live (resident) compilations into the store, so a symbol an edit introduces becomes
// queryable without a full re-index. Uses a raw Roslyn compilation over on-disk files (no MSBuildWorkspace, no
// Basic.CompilerLog - so no co-activation), projects it into a temp store, then edits (adds a type), re-projects,
// and confirms the new type's symbol is in the store.
public sealed class ProjectFromCompilationsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Projecting_resident_compilations_writes_symbols_and_reflects_edits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-project-compilations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var databasePath = Path.Combine(dir, ".fuse", "fuse.db");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Foo.cs"), "namespace Demo; public sealed class Foo { public int A() => 1; }", Ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "Bar.cs"), "namespace Demo; public sealed class Bar { public int B(Foo f) => f.A(); }", Ct);

            var projectPath = Path.Combine(dir, "Demo.csproj");
            var indexer = CreateIndexer();

            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(Ct);

            // First projection: Foo and Bar are queryable.
            await indexer.ProjectFromCompilationsAsync(dir, store, [(projectPath, BuildCompilation(dir, "Foo.cs", "Bar.cs"))], Files(dir, "Foo.cs", "Bar.cs"), Ct);
            var namesAfterFirst = (await store.ListSymbolsAsync(500, Ct)).Select(s => s.Name).ToHashSet();
            Assert.Contains("Foo", namesAfterFirst);
            Assert.Contains("Bar", namesAfterFirst);
            Assert.DoesNotContain("Baz", namesAfterFirst);

            // Edit: add Baz.cs on disk, rebuild the compilation, re-project. The new type is now queryable without
            // a full re-index (the resident-to-store projection reflected the edit).
            await File.WriteAllTextAsync(Path.Combine(dir, "Baz.cs"), "namespace Demo; public sealed class Baz { public int C() => 3; }", Ct);
            await indexer.ProjectFromCompilationsAsync(dir, store, [(projectPath, BuildCompilation(dir, "Foo.cs", "Bar.cs", "Baz.cs"))], Files(dir, "Foo.cs", "Bar.cs", "Baz.cs"), Ct);
            var namesAfterEdit = (await store.ListSymbolsAsync(500, Ct)).Select(s => s.Name).ToHashSet();
            Assert.Contains("Baz", namesAfterEdit);
            Assert.Contains("Foo", namesAfterEdit);

            // Removal case: rewrite Bar.cs to rename its type, then re-project. The clear-then-reproject drops the
            // stale "Bar" symbol (it is not merely left behind by an upsert).
            await File.WriteAllTextAsync(Path.Combine(dir, "Bar.cs"), "namespace Demo; public sealed class Renamed { public int B(Foo f) => f.A(); }", Ct);
            await indexer.ProjectFromCompilationsAsync(dir, store, [(projectPath, BuildCompilation(dir, "Foo.cs", "Bar.cs", "Baz.cs"))], Files(dir, "Foo.cs", "Bar.cs", "Baz.cs"), Ct);
            var namesAfterRemoval = (await store.ListSymbolsAsync(500, Ct)).Select(s => s.Name).ToHashSet();
            Assert.Contains("Renamed", namesAfterRemoval);
            Assert.DoesNotContain("Bar", namesAfterRemoval);

            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static CSharpCompilation BuildCompilation(string dir, params string[] fileNames)
    {
        var trees = fileNames.Select(f =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(dir, f)), path: Path.Combine(dir, f))).ToArray();
        return CSharpCompilation.Create("Demo", trees, ReferencePaths(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IReadOnlyList<IndexedFileRecord> Files(string dir, params string[] fileNames) =>
        fileNames.Select(f => new IndexedFileRecord(
            Path: Path.Combine(dir, f),
            NormalizedPath: f,
            Extension: ".cs",
            SizeBytes: new FileInfo(Path.Combine(dir, f)).Length,
            MtimeUtcTicks: 0,
            ContentHash: "hash-" + f)).ToList();

    private static ImmutableArray<MetadataReference> ReferencePaths()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
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
}
