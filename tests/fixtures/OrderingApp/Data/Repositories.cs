namespace OrderingApp.Data;

// R6 edge-case wiring: open generic DI, a TryAdd registration, and a multiple-implementation ambiguity.

// Open generic: AddScoped(typeof(IRepository<>), typeof(Repository<>)).
public interface IRepository<T>
{
    T? Get(int id);
}

public sealed class Repository<T> : IRepository<T>
{
    public T? Get(int id) => default;
}

// TryAdd: TryAddSingleton<ICache, MemoryCache>().
public interface ICache
{
    object? Lookup(string key);
}

public sealed class MemoryCache : ICache
{
    public object? Lookup(string key) => null;
}

// Multiple-implementation ambiguity: two implementations exist, but only FastShipping is registered, so the
// resolver must pick FastShipping and must not emit a resolve edge to SlowShipping.
public interface IShipping
{
    int Quote(int weight);
}

public sealed class FastShipping : IShipping
{
    public int Quote(int weight) => weight * 2;
}

public sealed class SlowShipping : IShipping
{
    public int Quote(int weight) => weight;
}
