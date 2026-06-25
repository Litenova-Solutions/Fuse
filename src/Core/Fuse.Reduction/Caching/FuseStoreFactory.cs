using Microsoft.Extensions.Logging;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Default factory for SQLite-backed fuse stores.
/// </summary>
public sealed class FuseStoreFactory : IFuseStoreFactory
{
    private readonly ILogger<SqliteKeyValueStore>? _logger;

    /// <summary>
    ///     Creates a factory that passes an optional logger into opened stores.
    /// </summary>
    /// <param name="logger">Optional logger for flush exhaustion warnings on opened stores.</param>
    public FuseStoreFactory(ILogger<SqliteKeyValueStore>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IKeyValueStore Open(string sourceDirectory) =>
        new SqliteKeyValueStore(FuseStorePaths.ResolveDatabasePath(sourceDirectory), _logger);
}
