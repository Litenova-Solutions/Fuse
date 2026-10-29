using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A cold check of the rename break under load: six fresh repositories while two processors spin, each answering
///     both dependent breaks. With the output pumps awaited, a truncated capture can no longer hide a break.
/// </summary>
public class ColdCheckUnderLoadTests
{
    [Fact]
    public async Task Check_reports_the_break_on_a_cold_engine_under_load()
    {
        using var spinning = new CancellationTokenSource();
        var workers = new[] { Task.Run(() => Spin(spinning.Token), TestContext.Current.CancellationToken), Task.Run(() => Spin(spinning.Token), TestContext.Current.CancellationToken) };
        try
        {
            for (var i = 0; i < 6; i++)
            {
                using var repo = FixtureRepo.CreateStandard();
                try
                {
                    repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
                    var result = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs");
                    // The whole output on failure: xUnit cuts a Contains failure to its first line.
                    Assert.True(
                        result.ExitCode == 1
                        && result.Stdout.Contains("App/Program.cs", StringComparison.Ordinal)
                        && result.Stdout.Contains("Lib.Tests/CalcTests.cs", StringComparison.Ordinal),
                        $"fuse check printed:\n{result.Stdout}\n{result.Stderr}");
                }
                finally
                {
                    await FuseProcess.StopEngineAsync(repo.Root);
                }
            }
        }
        finally
        {
            await spinning.CancelAsync();
            await Task.WhenAll(workers);
        }
    }

    private static void Spin(CancellationToken cancellationToken)
    {
        var value = 0;
        while (!cancellationToken.IsCancellationRequested)
            value = (value * 31 + 17) & 0x7ffffff;
        GC.KeepAlive(value);
    }
}
