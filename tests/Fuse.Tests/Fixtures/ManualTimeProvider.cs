namespace Fuse.Tests.Fixtures;

/// <summary>A clock the tests move by hand, for expiries and debounces.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount)
    {
        _now = _now.Add(amount);
    }
}
