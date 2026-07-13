using System.Diagnostics;
using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// C4/D20: the per-repo hard timeout for the corpus-health sweep. These pin that fast work returns normally, that
// a stalling repo is converted to a RepoTimeoutException (record-and-continue) rather than wedging the sweep, and
// that an outer cancel still propagates as OperationCanceledException (stop the sweep).
public sealed class CorpusHealthTimeoutTests
{
    [Fact]
    public async Task Fast_work_returns_its_result_within_budget()
    {
        var result = await CorpusHealthSuite.RunWithRepoTimeoutAsync(
            _ => Task.FromResult("semantic"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal("semantic", result);
    }

    [Fact]
    public async Task Null_timeout_runs_unbounded()
    {
        var result = await CorpusHealthSuite.RunWithRepoTimeoutAsync(
            _ => Task.FromResult("syntax"),
            timeout: null,
            CancellationToken.None);

        Assert.Equal("syntax", result);
    }

    [Fact]
    public async Task Work_exceeding_the_budget_throws_RepoTimeoutException()
    {
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<RepoTimeoutException>(async () =>
            await CorpusHealthSuite.RunWithRepoTimeoutAsync<string>(
                async ct =>
                {
                    // Simulates a stalling restore/index: honors the token, so the budget bites.
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return "never";
                },
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None));

        stopwatch.Stop();
        // The budget must actually fire quickly, not run the full simulated stall.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"timeout did not bite promptly: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Outer_cancellation_propagates_as_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CorpusHealthSuite.RunWithRepoTimeoutAsync<string>(
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return "never";
                },
                TimeSpan.FromMinutes(10),
                cts.Token));
    }
}
