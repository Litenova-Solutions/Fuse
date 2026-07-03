using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// M1: the speculative staging area. A session stages edits in memory, diagnoses them (R1, abstaining without a
// worker), selects covering tests (R5), and touches the tree only on an explicit promote; two sessions over the
// same base are isolated; a discard leaves the tree untouched.
public sealed class ChangesetSessionStoreTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-changeset-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private WorkspaceIndexStore _store = null!;

    public ChangesetSessionStoreTests()
        => _databasePath = Path.Combine(_root, ".fuse", "fuse.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public void Two_sessions_over_the_same_base_are_isolated()
    {
        var store = new ChangesetSessionStore();
        var a = store.Create(_root);
        var b = store.Create(_root);

        store.Stage(a, "src/File.cs", "// version A");
        store.Stage(b, "src/Other.cs", "// version B");

        Assert.Equal(["src/File.cs"], store.StagedFiles(a));
        Assert.Equal(["src/Other.cs"], store.StagedFiles(b));
    }

    [Fact]
    public void Stage_on_an_unknown_session_returns_false()
        => Assert.False(new ChangesetSessionStore().Stage("nope", "a.cs", "x"));

    [Fact]
    public async Task Promote_writes_the_staged_edits_to_the_tree()
    {
        var store = new ChangesetSessionStore();
        var id = store.Create(_root);
        store.Stage(id, "src/Widget.cs", "public class Widget { }");

        var written = await store.PromoteAsync(id, CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal(["src/Widget.cs"], written);
        var onDisk = await File.ReadAllTextAsync(Path.Combine(_root, "src", "Widget.cs"));
        Assert.Equal("public class Widget { }", onDisk);
        // The session is consumed by promote.
        Assert.Null(store.StagedFiles(id));
    }

    [Fact]
    public async Task Discard_leaves_the_tree_untouched()
    {
        var store = new ChangesetSessionStore();
        var id = store.Create(_root);
        store.Stage(id, "src/Ghost.cs", "public class Ghost { }");

        Assert.True(store.Discard(id));

        Assert.False(File.Exists(Path.Combine(_root, "src", "Ghost.cs")));
        Assert.Null(store.StagedFiles(id));
    }

    [Fact]
    public async Task Diagnose_abstains_per_file_when_no_build_capture_worker_is_configured()
    {
        var store = new ChangesetSessionStore();
        var id = store.Create(_root);
        store.Stage(id, "src/A.cs", "public class A { }");

        // No FUSE_BUILD_CAPTURE_WORKER: the client is not available, so each file abstains rather than guessing.
        var client = new BuildCaptureClient(workerDllPath: null);
        var results = await store.DiagnoseAsync(id, Path.Combine(_root, "App.sln"), client, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(results);
        var one = Assert.Single(results!);
        Assert.False(one.Check.Verified);
        Assert.False(string.IsNullOrEmpty(one.Check.Reason));
    }

    [Fact]
    public async Task Select_covering_tests_returns_the_tests_edge_sources_for_the_changed_file()
    {
        // Seed a changed file with a type, and a test that carries a tests edge to it.
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("tests/OrderServiceTests.cs", "tests/OrderServiceTests.cs", ".cs", 10, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
                new NodeRecord("type:OrderServiceTests", "class", "OrderServiceTests", "App.Tests.OrderServiceTests", "tests/OrderServiceTests.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:OrderServiceTests", "type:OrderService", "tests", 0.9, 1.0)],
            CancellationToken.None);

        var store = new ChangesetSessionStore();
        var id = store.Create(_root);
        store.Stage(id, "src/OrderService.cs", "public class OrderService { public int V => 2; }");

        var covering = await store.SelectCoveringTestsAsync(id, _store, new GraphNeighborhoodExplorer(_store), 20, CancellationToken.None);

        Assert.NotNull(covering);
        Assert.Contains("tests/OrderServiceTests.cs", covering!);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
