using Fuse.Engine;

namespace Fuse.Tests.Unit;

/// <summary>
///     An engine returns its free memory once per idle period and exits after a longer idle time, never while it serves a
///     request.
/// </summary>
public class IdleClockTests
{
    [Fact]
    public void Memory_is_released_once_per_idle_period()
    {
        var time = new ManualClock();
        var clock = new IdleClock(time);

        time.Advance(IdleClock.ReleaseAfter - TimeSpan.FromSeconds(1));
        Assert.False(clock.TakeRelease());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(clock.TakeRelease());
        Assert.False(clock.TakeRelease());

        // A request starts a new idle period, which releases again.
        clock.Begin();
        clock.End();
        time.Advance(IdleClock.ReleaseAfter);
        Assert.True(clock.TakeRelease());
    }

    [Fact]
    public void A_running_request_keeps_the_engine_from_releasing_and_exiting()
    {
        var time = new ManualClock();
        var clock = new IdleClock(time);

        clock.Begin();
        time.Advance(IdleClock.ExitAfter + TimeSpan.FromMinutes(1));

        Assert.False(clock.TakeRelease());
        Assert.False(clock.ShouldExit);

        // The idle time counts from the end of the request, not its start.
        clock.End();
        Assert.False(clock.ShouldExit);
        time.Advance(IdleClock.ExitAfter);
        Assert.True(clock.ShouldExit);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan by) => _now += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
