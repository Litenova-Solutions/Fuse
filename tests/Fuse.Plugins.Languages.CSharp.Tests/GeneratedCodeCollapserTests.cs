using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Reducers;

namespace Fuse.Plugins.Languages.CSharp.Tests.Reducers;

public class GeneratedCodeCollapserTests
{
    private const string Migration = """
        using Microsoft.EntityFrameworkCore.Migrations;

        public partial class AddOrders : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.CreateTable(name: "Orders", columns: table => new { Id = table.Column<int>() });
                migrationBuilder.CreateIndex("IX_Orders_Id", "Orders", "Id");
            }

            protected override void Down(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropTable(name: "Orders");
            }
        }
        """;

    [Fact]
    public void IsGenerated_DetectsMigration()
    {
        Assert.True(GeneratedCodeCollapser.IsGenerated(Migration));
        Assert.False(GeneratedCodeCollapser.IsGenerated("public class Order { public int Id { get; set; } }"));
    }

    [Fact]
    public void Collapse_KeepsSignaturesDropsBodies()
    {
        var result = GeneratedCodeCollapser.Collapse(Migration);

        Assert.Contains("class AddOrders", result);
        Assert.Contains("void Up(MigrationBuilder", result);
        Assert.Contains("void Down(MigrationBuilder", result);
        Assert.Contains("collapsed generated body", result);
        Assert.DoesNotContain("CreateTable", result);
        Assert.DoesNotContain("DropTable", result);
    }

    [Fact]
    public void Collapse_NonGenerated_ReturnsUnchanged()
    {
        const string ordinary = """
            public class OrderService
            {
                public void Up() { DoRealWork(); }
            }
            """;

        // No EF markers, so the file is left untouched even though it has a method named Up.
        Assert.Equal(ordinary, GeneratedCodeCollapser.Collapse(ordinary));
    }

    [Fact]
    public void CSharpReducer_CollapseGeneratedOption_ReducesMigrationTokens()
    {
        var reducer = new CSharpReducer();
        var collapsed = reducer.Reduce(Migration, new ReductionOptions(collapseGeneratedCode: true));
        var full = reducer.Reduce(Migration, new ReductionOptions(collapseGeneratedCode: false));

        Assert.True(collapsed.Length < full.Length);
        Assert.DoesNotContain("CreateTable", collapsed);
    }
}
