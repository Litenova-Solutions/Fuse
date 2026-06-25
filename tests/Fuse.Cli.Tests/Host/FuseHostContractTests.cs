using System.Text.Json;
using Fuse.Cli.Rpc;

namespace Fuse.Cli.Tests;

// Pins the host RPC wire contract: the DTOs must serialize through the source-generated FuseHostJsonContext with
// camelCase property names, which is exactly what protocol.ts in the extension mirrors. A change to a DTO shape
// here fails this test, signalling that protocol.ts (and the extension contract test) must change in lockstep.
public sealed class FuseHostContractTests
{
    [Fact]
    public void Handshake_SerializesCamelCaseThroughSourceGenContext()
    {
        var json = JsonSerializer.Serialize(
            new FuseHostHandshake("3.0.0", FuseHostService.ProtocolVersion),
            FuseHostJsonContext.Default.FuseHostHandshake);

        Assert.Contains("\"hostVersion\":\"3.0.0\"", json);
        Assert.Contains("\"protocolVersion\":", json);
    }

    [Fact]
    public void Stats_RoundTripsThroughSourceGenContext()
    {
        var original = new FuseHostStats("3.0.0", 4242, 1500, 123_456_789);

        var json = JsonSerializer.Serialize(original, FuseHostJsonContext.Default.FuseHostStats);
        var back = JsonSerializer.Deserialize(json, FuseHostJsonContext.Default.FuseHostStats);

        Assert.Contains("\"workingSetBytes\":123456789", json);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Graph_SerializesNodesAndEdgesCamelCase()
    {
        var graph = new GraphDto(
            [new GraphNodeDto("a/B.cs", ["B"], 0.5, 1200, "Seed")],
            [new GraphEdgeDto("a/B.cs", "a/C.cs", 1.0, "reference")],
            "Files");

        var json = JsonSerializer.Serialize(graph, FuseHostJsonContext.Default.GraphDto);

        Assert.Contains("\"declaredTypes\":[\"B\"]", json);
        Assert.Contains("\"tokenCost\":1200", json);
        Assert.Contains("\"kind\":\"reference\"", json);
        Assert.Contains("\"detail\":\"Files\"", json);
    }

    [Fact]
    public void Node_OmitsNullRole()
    {
        // The optional role is written only when a scope is active, so an unscoped graph node carries no role key.
        var json = JsonSerializer.Serialize(
            new GraphNodeDto("a.cs", [], 0.0, 0, null), FuseHostJsonContext.Default.GraphNodeDto);

        Assert.DoesNotContain("role", json);
    }

    [Fact]
    public void ScopeResult_SerializesFilesAndTotalsCamelCase()
    {
        var scope = new ScopeResultDto(
            "search", [new ScopeFileDto("a/B.cs", 480)], 480, "/tmp/x.fuse.xml");

        var json = JsonSerializer.Serialize(scope, FuseHostJsonContext.Default.ScopeResultDto);

        Assert.Contains("\"mode\":\"search\"", json);
        Assert.Contains("\"tokenCost\":480", json);
        Assert.Contains("\"totalTokens\":480", json);
        Assert.Contains("\"payloadPath\":\"/tmp/x.fuse.xml\"", json);
    }

    [Fact]
    public void ScopeResult_OmitsNullPayloadPath()
    {
        var json = JsonSerializer.Serialize(
            new ScopeResultDto("search", [], 0, null), FuseHostJsonContext.Default.ScopeResultDto);

        Assert.DoesNotContain("payloadPath", json);
    }

    [Fact]
    public void Diagnostics_SerializesSecretRangesCamelCase()
    {
        var diagnostics = new DiagnosticsDto(
            [new SecretDiagnosticDto("a/Config.cs", "github-token", 12, 4, 12, 44)]);

        var json = JsonSerializer.Serialize(diagnostics, FuseHostJsonContext.Default.DiagnosticsDto);

        Assert.Contains("\"kind\":\"github-token\"", json);
        Assert.Contains("\"startLine\":12", json);
        Assert.Contains("\"endColumn\":44", json);
    }
}
