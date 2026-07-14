using Fuse.Context;
using Fuse.Scoping;
using Xunit;

namespace Fuse.Context.Tests;

// P7.3: XML/Markdown/JSON emission of a rendered context.
public sealed class SemanticContextEmitterTests
{
    [Fact]
    public void XmlEmitsManifestCommentAndFileElements()
    {
        var output = SemanticContextEmitter.Emit(Plan(), Rendered(), ContextOutputFormat.Xml, root: "/repo");

        Assert.Contains("<!--", output, StringComparison.Ordinal);
        Assert.Contains("fuse:semantic-context", output);
        Assert.Contains("<file path=\"src/OrderService.cs\"", output);
        Assert.Contains("public class OrderService", output);
        Assert.Contains("</file>", output);
    }

    [Fact]
    public void MarkdownEmitsHeadingsAndFencedCode()
    {
        var output = SemanticContextEmitter.Emit(Plan(), Rendered(), ContextOutputFormat.Markdown, root: "/repo");

        Assert.Contains("# Fuse semantic context", output);
        Assert.Contains("## src/OrderService.cs", output);
        Assert.Contains("```csharp", output);
    }

    [Fact]
    public void JsonEmitsCamelCaseFieldsAndContent()
    {
        var output = SemanticContextEmitter.Emit(Plan(), Rendered(), ContextOutputFormat.Json, root: "/repo");

        Assert.Contains("\"mode\": \"context\"", output);
        Assert.Contains("\"entries\"", output);
        Assert.Contains("\"path\": \"src/OrderService.cs\"", output);
        Assert.Contains("public class OrderService", output);
    }

    [Fact]
    public void XmlIncludesTheApiDeltaSectionWhenProvided()
    {
        var output = SemanticContextEmitter.Emit(
            Plan(), Rendered(), ContextOutputFormat.Xml, root: "/repo",
            apiDeltaSection: "public API delta: 1 BREAKING change(s), 0 additive.\n  [BREAKING] Api.Bar: removed");

        Assert.Contains("public API delta: 1 BREAKING change(s)", output);
        Assert.Contains("[BREAKING] Api.Bar: removed", output);
    }

    [Fact]
    public void JsonPutsTheApiDeltaInItsOwnFieldNotAsRawPrefix()
    {
        var output = SemanticContextEmitter.Emit(
            Plan(), Rendered(), ContextOutputFormat.Json, root: "/repo",
            apiDeltaSection: "public API delta: none");

        // The section rides a dedicated field so the payload stays valid JSON (a raw prefix would break parsing).
        Assert.Contains("\"apiDelta\"", output);
        Assert.StartsWith("{", output.TrimStart());
    }

    [Fact]
    public void JsonOmitsTheApiDeltaFieldWhenAbsent()
    {
        var output = SemanticContextEmitter.Emit(Plan(), Rendered(), ContextOutputFormat.Json, root: "/repo");
        Assert.DoesNotContain("\"apiDelta\"", output);
    }

    private static ContextPlan Plan() =>
        new("context",
        [
            new ContextPlanItem("src/OrderService.cs", "type:App.OrderService", "exact-seed", RenderTier.FullSource, 1.0, 10, true, ["seed"], ["seed"]),
        ], [], 10, []);

    private static RenderedContext Rendered() =>
        new(
        [
            new RenderedFile("src/OrderService.cs", "exact-seed", RenderTier.FullSource, 1.0, "public class OrderService { }", 10, ["seed"]),
        ], 10);
}
