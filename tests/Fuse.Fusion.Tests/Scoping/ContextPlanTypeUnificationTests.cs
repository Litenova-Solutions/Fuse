using Fuse.Fusion;
using Fuse.Retrieval;
using Fuse.Scoping;
using Xunit;

namespace Fuse.Fusion.Tests.Scoping;

/// <summary>
///     R10 gate: fusion focus scoping and MCP review must share the same <see cref="ContextPlan" /> type.
/// </summary>
public sealed class ContextPlanTypeUnificationTests
{
    [Fact]
    public void FusionAndMcpReview_UseSameContextPlanType()
    {
        var fusionReturn = typeof(ContextPlanBuilder).GetMethod(nameof(ContextPlanBuilder.Build))!.ReturnType;
        var reviewReturn = typeof(SemanticRetrievalEngine)
            .GetMethod(nameof(SemanticRetrievalEngine.ReviewAsync))!
            .ReturnType
            .GetGenericArguments()[0];

        Assert.Same(typeof(ContextPlan), fusionReturn);
        Assert.Same(typeof(ContextPlan), reviewReturn);
    }
}
