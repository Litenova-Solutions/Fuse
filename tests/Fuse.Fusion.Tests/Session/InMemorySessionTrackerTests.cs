using Fuse.Fusion.Session;

namespace Fuse.Fusion.Tests.Session;

public class InMemorySessionTrackerTests
{
    [Fact]
    public void Claim_FirstTime_IsNew()
    {
        var tracker = new InMemorySessionTracker();
        Assert.Equal(SessionEntryStatus.New, tracker.Claim("s1", "a.cs", 100, "body").Status);
    }

    [Fact]
    public void Claim_SameContentTwice_IsUnchangedSecondTime()
    {
        var tracker = new InMemorySessionTracker();
        tracker.Claim("s1", "a.cs", 100, "body");
        Assert.Equal(SessionEntryStatus.Unchanged, tracker.Claim("s1", "a.cs", 100, "body").Status);
    }

    [Fact]
    public void Claim_ChangedContent_IsChangedWithPriorContent()
    {
        var tracker = new InMemorySessionTracker();
        tracker.Claim("s1", "a.cs", 100, "old body");

        var claim = tracker.Claim("s1", "a.cs", 200, "new body");

        Assert.Equal(SessionEntryStatus.Changed, claim.Status);
        Assert.Equal("old body", claim.PriorContent);
        // The new hash is now the baseline.
        Assert.Equal(SessionEntryStatus.Unchanged, tracker.Claim("s1", "a.cs", 200, "new body").Status);
    }

    [Fact]
    public void Claim_ChangedContent_OversizedPrior_FallsBackToNoPrior()
    {
        var tracker = new InMemorySessionTracker();
        var huge = new string('x', 70 * 1024); // above the retention cap
        tracker.Claim("s1", "a.cs", 100, huge);

        var claim = tracker.Claim("s1", "a.cs", 200, "small");

        Assert.Equal(SessionEntryStatus.Changed, claim.Status);
        Assert.Null(claim.PriorContent); // not retained, so the caller resends the whole file
    }

    [Fact]
    public void Claim_DifferentSessions_AreIndependent()
    {
        var tracker = new InMemorySessionTracker();
        tracker.Claim("s1", "a.cs", 100, "body");
        Assert.Equal(SessionEntryStatus.New, tracker.Claim("s2", "a.cs", 100, "body").Status);
    }

    [Fact]
    public void Reset_ClearsSession()
    {
        var tracker = new InMemorySessionTracker();
        tracker.Claim("s1", "a.cs", 100, "body");
        tracker.Reset("s1");
        Assert.Equal(SessionEntryStatus.New, tracker.Claim("s1", "a.cs", 100, "body").Status);
    }
}
