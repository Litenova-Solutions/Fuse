using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class SessionStoreUnitTests
{
    [Fact]
    public void SessionSummary_record_preserves_flags()
    {
        var summary = new SessionSummary("sess-1", "2026-01-01T00:00:00Z", HasBaseline: true, HasClaims: false);

        Assert.Equal("sess-1", summary.SessionId);
        Assert.True(summary.HasBaseline);
        Assert.False(summary.HasClaims);
    }
}
