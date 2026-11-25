using Fuse.Graph;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Unit;

public class BlueprintExtractionTests
{
    [Fact]
    public async Task Concurrent_extractions_share_one_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        var graph = await RepoGraph.EvaluateAsync(engine.Repo.Root, cancellationToken);
        var app = graph.Projects.Single(p => p.Name == "App");

        var evaluations = 0;
        void Log(string line)
        {
            if (line.Contains("blueprint evaluated", StringComparison.Ordinal))
                Interlocked.Increment(ref evaluations);
        }

        var first = BlueprintEvaluator.EnsureProjectsAsync(engine.Repo.Root, graph, [app], Log, cancellationToken);
        var second = BlueprintEvaluator.EnsureProjectsAsync(engine.Repo.Root, graph, [app], Log, cancellationToken);
        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, o => Assert.NotNull(o.Blueprint));
        Assert.All(outcomes, o => Assert.Empty(o.Failures));
        Assert.Equal(1, evaluations);
    }
}
