namespace OrderingApp.Ordering;

// A factory-registered service (R5: AddSingleton<IClock>(sp => new SystemClock())).
public interface IClock
{
    long NowTicks();
}

public sealed class SystemClock : IClock
{
    public long NowTicks() => 0;
}
