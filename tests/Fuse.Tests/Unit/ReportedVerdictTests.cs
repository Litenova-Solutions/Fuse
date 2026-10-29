using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     A reported line without a verdict never reads as a pass: the missing value is unknown, which the engine stores
///     as a skip and never serves from the cache.
/// </summary>
public class ReportedVerdictTests
{
    [Fact]
    public void The_missing_verdict_is_unknown_not_passed()
    {
        Assert.Equal(ReportedVerdict.Unknown, default(ReportedVerdict));
        Assert.NotEqual(ReportedVerdict.Passed, default(ReportedVerdict));
    }

    [Fact]
    public void A_line_without_a_verdict_deserializes_as_unknown()
    {
        var request = ProtocolJson.ReadRequest("""{"request":"ReportTestResults","buildId":"test","results":[{"test":"T","project":"P"}]}""") as EngineRequest.ReportTestResults;

        Assert.NotNull(request);
        Assert.Equal(ReportedVerdict.Unknown, Assert.Single(request.Results).Verdict);
    }
}
