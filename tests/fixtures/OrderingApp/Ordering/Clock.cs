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

// A keyed-DI service (G2: AddKeyedScoped<INotifier, EmailNotifier>("email")); the key does not change the
// service -> implementation resolution the analyzer extracts.
public interface INotifier
{
    void Notify(string message);
}

public sealed class EmailNotifier : INotifier
{
    public void Notify(string message) { }
}
