using Fuse.Context;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Context.Tests;

// P9.2: session tracking elides unchanged files on a later send.
public sealed class ContextSessionStoreTests
{
    private readonly ContextSessionStore _store = new();

    [Fact]
    public void FirstSendReportsNothingUnchanged()
    {
        var unchanged = _store.Reconcile("s1", [File("a.cs", "class A {}")]);

        Assert.Empty(unchanged);
    }

    [Fact]
    public void ResendingSameContentMarksItUnchanged()
    {
        _store.Reconcile("s1", [File("a.cs", "class A {}")]);

        var unchanged = _store.Reconcile("s1", [File("a.cs", "class A {}")]);

        Assert.Contains("a.cs", unchanged);
    }

    [Fact]
    public void ChangedContentIsNotUnchanged()
    {
        _store.Reconcile("s1", [File("a.cs", "class A {}")]);

        var unchanged = _store.Reconcile("s1", [File("a.cs", "class A { void M(){} }")]);

        Assert.DoesNotContain("a.cs", unchanged);
    }

    [Fact]
    public void SessionsAreIndependent()
    {
        _store.Reconcile("s1", [File("a.cs", "class A {}")]);

        var unchanged = _store.Reconcile("s2", [File("a.cs", "class A {}")]);

        Assert.Empty(unchanged);
    }

    [Fact]
    public void EmitterOmitsBodyForUnchangedFile()
    {
        var plan = new ContextPlan("context",
            [new ContextPlanItem("a.cs", null, "dependency", RenderTier.FullSource, 0.5, 5, false, [], [])], [], 5, []);
        var rendered = new RenderedContext([new RenderedFile("a.cs", "dependency", RenderTier.FullSource, 0.5, "class A {}", 5, [])], 5);

        var output = SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, unchangedPaths: ["a.cs"]);

        Assert.Contains("unchanged=\"true\"", output);
        Assert.DoesNotContain("class A {}", output);
    }

    private static RenderedFile File(string path, string content) =>
        new(path, "dependency", RenderTier.FullSource, 0.5, content, 5, []);
}
