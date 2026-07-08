using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R6: a repair packet turns a speculative-typecheck diagnostic into fix context. A CS1061 (missing member)
// returns the receiver type's real members with the nearest name first; a CS0246 (unknown type) returns the
// nearest type names; any other diagnostic returns null so the check output is not padded.
public sealed class RepairPacketBuilderTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-repair-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;
    private RepairPacketBuilder _builder = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/Order.cs", "src/Order.cs", ".cs", 60, 1, "h1")],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [
                new SymbolRecord("symbol:Shop.Order", "src/Order.cs", "class", "Order", "Shop.Order",
                    Accessibility: "public", Signature: "public sealed class Order", StartLine: 1, EndLine: 20, IsPublicApi: true),
                new SymbolRecord("symbol:Shop.Order.Total", "src/Order.cs", "property", "Total", "Shop.Order.Total",
                    ContainingType: "Shop.Order", Accessibility: "public", Signature: "public decimal Total { get; }", StartLine: 5, EndLine: 5, IsPublicApi: true),
                new SymbolRecord("symbol:Shop.Order.GrandTotal", "src/Order.cs", "method", "GrandTotal", "Shop.Order.GrandTotal",
                    ContainingType: "Shop.Order", Accessibility: "public", Signature: "public decimal GrandTotal()", StartLine: 6, EndLine: 6, IsPublicApi: true),
                new SymbolRecord("symbol:Shop.Invoice", "src/Order.cs", "class", "Invoice", "Shop.Invoice",
                    Accessibility: "public", Signature: "public sealed class Invoice", StartLine: 22, EndLine: 30, IsPublicApi: true),
            ],
            CancellationToken.None);
        _builder = new RepairPacketBuilder(_store);
    }

    [Fact]
    public async Task Missing_member_suggests_the_nearest_real_member()
    {
        // The typo "GrandTotol" is one edit from the real "GrandTotal", which must lead the candidates.
        var diagnostic = new CheckDiagnostic("CS1061",
            "Error", "'Shop.Order' does not contain a definition for 'GrandTotol'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal("CS1061", packet!.DiagnosticId);
        Assert.Equal("GrandTotal", packet.Candidates.First());
        Assert.Contains(packet.Members, m => m.Name == "GrandTotal");
        Assert.Contains(packet.Members, m => m.Name == "Total");
    }

    [Fact]
    public async Task Unknown_type_suggests_the_nearest_type_name()
    {
        var diagnostic = new CheckDiagnostic("CS0246",
            "Error", "The type or namespace name 'Invoce' could not be found", "src/Order.cs", 10);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Contains("Invoice", packet!.Candidates);
    }

    [Fact]
    public async Task Missing_member_on_an_unindexed_type_explains_the_gap_without_guessing()
    {
        var diagnostic = new CheckDiagnostic("CS1061",
            "Error", "'System.String' does not contain a definition for 'Frobnicate'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Empty(packet!.Candidates);
        Assert.Contains("referenced assembly", packet.Explanation);
    }

    [Fact]
    public async Task Static_member_access_CS0117_suggests_the_nearest_real_member()
    {
        // CS0117 shares CS1061's message shape ("'Type' does not contain a definition for 'Member'") for
        // static/type-level access, so it lists the receiver type's real members with the nearest name first.
        var diagnostic = new CheckDiagnostic("CS0117",
            "Error", "'Shop.Order' does not contain a definition for 'GrandTotol'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal("CS0117", packet!.DiagnosticId);
        Assert.Equal("GrandTotal", packet.Candidates.First());
        Assert.Contains(packet.Members, m => m.Name == "GrandTotal");
    }

    [Fact]
    public async Task Missing_required_argument_CS7036_surfaces_the_callee_signature()
    {
        // The call omits a required parameter; the packet re-presents the callee's parameters so the agent knows
        // what to pass. GrandTotal is recorded, so its signature is surfaced.
        var diagnostic = new CheckDiagnostic("CS7036",
            "Error", "There is no argument given that corresponds to the required parameter 'value' of 'Shop.Order.GrandTotal(decimal)'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal("CS7036", packet!.DiagnosticId);
        Assert.Contains("required parameter 'value'", packet.Explanation);
        Assert.Contains(packet.Members, m => m.Name == "GrandTotal");
    }

    [Fact]
    public async Task Type_mismatch_CS0029_names_the_conversion_direction()
    {
        var diagnostic = new CheckDiagnostic("CS0029",
            "Error", "Cannot implicitly convert type 'string' to 'decimal'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal("CS0029", packet!.DiagnosticId);
        Assert.Contains("(decimal)", packet.Explanation);
    }

    [Fact]
    public async Task Type_mismatch_CS0029_suggests_a_source_member_that_yields_the_target()
    {
        // Shop.Order.Total yields decimal, so converting an Order where a decimal is wanted likely means .Total.
        var diagnostic = new CheckDiagnostic("CS0029",
            "Error", "Cannot implicitly convert type 'Shop.Order' to 'decimal'", "src/Order.cs", 6);

        var packet = await _builder.BuildAsync(diagnostic, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Contains("Total", packet!.Candidates);
    }

    [Fact]
    public async Task An_unhandled_diagnostic_returns_no_packet()
    {
        // CS0165 (use of unassigned local) has no nearest-name or signature suggestion, so no packet is built.
        var diagnostic = new CheckDiagnostic("CS0165",
            "Error", "Use of unassigned local variable 'x'", "src/Order.cs", 6);

        Assert.Null(await _builder.BuildAsync(diagnostic, CancellationToken.None));
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        var directory = Path.GetDirectoryName(_databasePath);
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
