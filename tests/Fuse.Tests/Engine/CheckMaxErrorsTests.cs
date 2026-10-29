using Fuse.Check;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class CheckMaxErrorsTests
{
    [Fact]
    public void A_check_request_carries_its_cap_and_reads_it_back()
    {
        var changes = Assert.IsType<EngineRequest.CheckChanges>(
            ProtocolJson.ReadRequest(ProtocolJson.Serialize(new EngineRequest.CheckChanges(true) { MaxErrors = 50, BuildId = "5.7.0/abc" })));
        var files = Assert.IsType<EngineRequest.CheckFiles>(
            ProtocolJson.ReadRequest(ProtocolJson.Serialize(new EngineRequest.CheckFiles(["Lib/Calc.cs"], true) { MaxErrors = int.MaxValue, BuildId = "5.7.0/abc" })));

        Assert.Equal(50, changes.MaxErrors);
        Assert.True(changes.WaitForLoad);
        Assert.Equal(int.MaxValue, files.MaxErrors);
        Assert.Equal(["Lib/Calc.cs"], files.Files);
    }

    [Fact]
    public void A_line_without_the_cap_reads_it_as_zero()
    {
        var changes = Assert.IsType<EngineRequest.CheckChanges>(ProtocolJson.ReadRequest("""{"request":"CheckChanges","waitForLoad":true}"""));
        var files = Assert.IsType<EngineRequest.CheckFiles>(ProtocolJson.ReadRequest("""{"request":"CheckFiles","files":[],"waitForLoad":true}"""));

        Assert.Equal(0, changes.MaxErrors);
        Assert.Equal(0, files.MaxErrors);
    }

    [Fact]
    public async Task The_cap_limits_only_the_errors_sent_not_the_counts()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var scope = engine.Files("Lib/Calc.cs");

        var capped = await engine.Checker.CheckAsync(scope, null, AnalyzerRun.Full, PhaseTimes.None, TestContext.Current.CancellationToken, maxReported: 1);
        var sent = await engine.Checker.CheckAsync(scope, null, AnalyzerRun.Full, PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.True(sent.ErrorCount > 1, $"expected several errors, found {sent.ErrorCount}");
        Assert.Equal(sent.ErrorCount, sent.Errors.Count);
        Assert.Single(capped.Errors);
        Assert.Equal(sent.ErrorCount, capped.ErrorCount);
        Assert.Equal(sent.ErrorFileCount, capped.ErrorFileCount);
    }
}
