using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The member walk refines the class-level answer up to 100 reachable test classes, and every plan that reaches it
///     records how the walk ended in the engine log.
/// </summary>
public class MemberWalkStatisticsTests
{
    private static Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files) =>
        SelectAsync(engine, new TestSelector(engine.Workspace, new SteppingClock()), files);

    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, TestSelector selector, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task Member_walk_past_forty_classes_selects_at_member_level()
    {
        await using var engine = await InProcessEngine.StartAsync(WithManyCallers());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 0;");
        var selection = await SelectAsync(engine, "Lib/Calc.cs");

        // 46 tests reach the member: Multiplies and the 45 generated callers, each selected by method name.
        var patterns = Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns;
        Assert.Equal(46, patterns.Count);
        Assert.DoesNotContain(patterns, p => p.EndsWith('.'));
        Assert.DoesNotContain("App.Tests", selection.Keys);
        Assert.Contains(
            engine.Log,
            line => line.StartsWith("test selection: member walk completed in ", StringComparison.Ordinal)
                && line.Contains("46 test class(es) reachable", StringComparison.Ordinal)
                && line.EndsWith("; selecting at member level", StringComparison.Ordinal));
    }

    /// <summary>The standard repository with 45 committed one-test classes in Lib.Tests that call <c>Calc.Mul</c>.</summary>
    private static FixtureRepo WithManyCallers()
    {
        var repo = FixtureRepo.CreateStandard();
        for (var i = 1; i <= 45; i++)
        {
            repo.Write($"Lib.Tests/Reach{i}.cs", $$"""
                using Lib;

                namespace Lib.Tests;

                public class Reach{{i}}Tests
                {
                    [Fact]
                    public void Reaches() => Assert.Equal(2, new Calc().Mul(1, 2));
                }
                """);
        }

        repo.Commit("add forty-five callers of Calc.Mul");
        return repo;
    }

    /// <summary>A clock that advances a millisecond per reading, so the walk never reaches its 8 second budget.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() => _now += TimestampFrequency / 1000;
    }
}
