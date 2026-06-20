using Fuse.Fusion.Session;

namespace Fuse.Fusion.Tests.Session;

public class InMemorySessionTrackerTests
{
    [Fact]
    public void TryClaim_FirstTime_ReturnsTrue()
    {
        var tracker = new InMemorySessionTracker();
        Assert.True(tracker.TryClaim("s1", "a.cs", 100));
    }

    [Fact]
    public void TryClaim_SameContentTwice_ReturnsFalseSecondTime()
    {
        var tracker = new InMemorySessionTracker();
        tracker.TryClaim("s1", "a.cs", 100);
        Assert.False(tracker.TryClaim("s1", "a.cs", 100));
    }

    [Fact]
    public void TryClaim_ChangedContent_ReturnsTrueAndUpdates()
    {
        var tracker = new InMemorySessionTracker();
        tracker.TryClaim("s1", "a.cs", 100);
        Assert.True(tracker.TryClaim("s1", "a.cs", 200));
        // The new hash is now the baseline.
        Assert.False(tracker.TryClaim("s1", "a.cs", 200));
    }

    [Fact]
    public void TryClaim_DifferentSessions_AreIndependent()
    {
        var tracker = new InMemorySessionTracker();
        tracker.TryClaim("s1", "a.cs", 100);
        Assert.True(tracker.TryClaim("s2", "a.cs", 100));
    }

    [Fact]
    public void Reset_ClearsSession()
    {
        var tracker = new InMemorySessionTracker();
        tracker.TryClaim("s1", "a.cs", 100);
        tracker.Reset("s1");
        Assert.True(tracker.TryClaim("s1", "a.cs", 100));
    }
}
