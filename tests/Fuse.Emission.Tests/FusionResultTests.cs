using Fuse.Emission.Models;

namespace Fuse.Emission.Tests;

/// <summary>
///     Verifies record semantics for <see cref="FusionResult" /> after the A-3 conversion from class to record.
/// </summary>
public sealed class FusionResultTests
{
    [Fact]
    public void WithExpression_PreservesPlanWhenUpdatingEmissionFields()
    {
        var plan = new List<PlannedFileInfo>
        {
            new("src/Alpha.cs", "seed", "full", 1.0),
            new("src/Beta.cs", "dependency", "skeleton", 0.4),
        };

        var original = new FusionResult([], "body", 10, 2, 2, TimeSpan.FromMilliseconds(5), [], plan: plan);
        var updated = original with { InMemoryContent = "body\nappended" };

        Assert.Equal(plan, updated.Plan);
        Assert.Equal("body\nappended", updated.InMemoryContent);
    }
}
