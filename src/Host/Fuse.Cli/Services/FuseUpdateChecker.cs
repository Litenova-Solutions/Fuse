using System.Text.Json;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Services;

/// <summary>
///     The result of an update check: the running version, the latest known version, and whether an upgrade is
///     available.
/// </summary>
/// <param name="CurrentVersion">The running Fuse version.</param>
/// <param name="LatestVersion">The latest version seen on NuGet, or null when unknown (no cache yet).</param>
/// <param name="UpdateAvailable">Whether <paramref name="LatestVersion" /> is newer than <paramref name="CurrentVersion" />.</param>
public sealed record UpdateStatus(string CurrentVersion, string? LatestVersion, bool UpdateAvailable);

/// <summary>
///     A throttled, cache-first, offline-safe check for a newer Fuse on NuGet. Reads a cached result instantly
///     (so a startup path is never delayed or made to depend on the network) and refreshes the cache in the
///     background at most once a day. Disabled entirely with <c>FUSE_UPDATE_CHECK=0</c>.
/// </summary>
/// <remarks>
///     The cache lives at <c>{user data}/update-check.json</c> (see <see cref="FuseStorePaths.GetUserDataDirectory" />).
///     Every network and disk operation is best-effort: any failure leaves the cache untouched and yields no
///     update information, so the check can never break a command or force a network dependency onto indexing.
/// </remarks>
public sealed class FuseUpdateChecker
{
    /// <summary>The environment variable that disables the update check when set to a falsy value.</summary>
    public const string DisableEnvironmentVariable = "FUSE_UPDATE_CHECK";

    private const string CacheFileName = "update-check.json";
    private const string FlatContainerUrl = "https://api.nuget.org/v3-flatcontainer/fuse/index.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(5);

    private readonly string _cachePath;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseUpdateChecker" /> class.
    /// </summary>
    /// <param name="cacheDirectory">The directory holding the cache file; defaults to the Fuse user data directory.</param>
    public FuseUpdateChecker(string? cacheDirectory = null) =>
        _cachePath = Path.Combine(cacheDirectory ?? FuseStorePaths.GetUserDataDirectory(), CacheFileName);

    /// <summary>Whether the update check is enabled (it is, unless <c>FUSE_UPDATE_CHECK</c> is a falsy value).</summary>
    public static bool IsEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
            return !(string.Equals(value, "0", StringComparison.Ordinal)
                     || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    ///     Returns the cached update status for the given running version, or null when nothing has been cached.
    ///     Reads only the local cache, so it is instant and offline.
    /// </summary>
    /// <param name="currentVersion">The running Fuse version to compare against.</param>
    /// <returns>The cached status, or null when the cache is absent or unreadable.</returns>
    public UpdateStatus? GetCachedStatus(string currentVersion)
    {
        var latest = ReadCachedLatest();
        if (string.IsNullOrWhiteSpace(latest))
            return null;
        return new UpdateStatus(currentVersion, latest, IsNewer(currentVersion, latest));
    }

    /// <summary>
    ///     Refreshes the cache from NuGet when it is stale. Best-effort: any failure (offline, timeout, parse)
    ///     leaves the cache untouched.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the network call.</param>
    /// <returns>A task that completes when the refresh finishes or is skipped.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (IsCacheFresh())
                return;

            using var http = new HttpClient { Timeout = NetworkTimeout };
            var json = await http.GetStringAsync(FlatContainerUrl, cancellationToken);
            var latest = SelectLatestStable(ParseVersions(json));
            if (latest is not null)
                SaveLatest(latest);
        }
        catch (Exception)
        {
            // Offline, timed out, or malformed: leave the cache as-is so the check never breaks anything.
        }
    }

    /// <summary>
    ///     Records the latest known version in the cache with the current timestamp.
    /// </summary>
    /// <param name="latestVersion">The latest version to cache.</param>
    public void SaveLatest(string latestVersion)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            using var stream = File.Create(_cachePath);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("checkedUtc", DateTimeOffset.UtcNow.ToString("o"));
            writer.WriteString("latest", latestVersion);
            writer.WriteEndObject();
        }
        catch (Exception)
        {
            // A cache write failure is not fatal; the next run simply refreshes again.
        }
    }

    /// <summary>Whether the cache exists and was written within the throttle window.</summary>
    /// <returns>True when a fresh cache entry is present.</returns>
    public bool IsCacheFresh()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            if (!doc.RootElement.TryGetProperty("checkedUtc", out var checkedAt)
                || !DateTimeOffset.TryParse(checkedAt.GetString(), out var when))
                return false;
            return DateTimeOffset.UtcNow - when < CacheTtl;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether <paramref name="latest" /> is a newer version than <paramref name="current" />, comparing the
    ///     numeric core (build metadata and prerelease suffixes are ignored).
    /// </summary>
    /// <param name="current">The current version string.</param>
    /// <param name="latest">The candidate newer version string.</param>
    /// <returns>True when both parse and <paramref name="latest" /> is strictly greater.</returns>
    public static bool IsNewer(string current, string latest)
    {
        var currentCore = ParseCore(current);
        var latestCore = ParseCore(latest);
        return currentCore is not null && latestCore is not null && latestCore > currentCore;
    }

    /// <summary>
    ///     Selects the highest stable (non-prerelease) version from a list, or null when none parse.
    /// </summary>
    /// <param name="versions">The candidate version strings.</param>
    /// <returns>The original string of the highest stable version, or null.</returns>
    public static string? SelectLatestStable(IReadOnlyList<string> versions)
    {
        string? best = null;
        Version? bestParsed = null;
        foreach (var version in versions)
        {
            if (version.Contains('-', StringComparison.Ordinal))
                continue; // Skip prereleases; the update check only advertises stable releases.
            var parsed = ParseCore(version);
            if (parsed is null)
                continue;
            if (bestParsed is null || parsed > bestParsed)
            {
                bestParsed = parsed;
                best = version;
            }
        }

        return best;
    }

    /// <summary>
    ///     Parses the version list from a NuGet flat-container <c>index.json</c> body.
    /// </summary>
    /// <param name="flatContainerJson">The response body from the flat-container index.</param>
    /// <returns>The listed version strings, or an empty list when the body is malformed.</returns>
    public static IReadOnlyList<string> ParseVersions(string flatContainerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(flatContainerJson);
            if (!doc.RootElement.TryGetProperty("versions", out var array) || array.ValueKind != JsonValueKind.Array)
                return [];

            var versions = new List<string>();
            foreach (var element in array.EnumerateArray())
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    versions.Add(value);
            }

            return versions;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string? ReadCachedLatest()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            return doc.RootElement.TryGetProperty("latest", out var latest) ? latest.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Reduces a version string to its numeric Major.Minor[.Patch[.Revision]] core, dropping any prerelease or
    // build-metadata suffix, so "3.1.2-beta+sha" compares as 3.1.2. Returns null when the core is not numeric.
    private static Version? ParseCore(string version)
    {
        var core = version.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];

        var parts = core.Split('.');
        if (parts.Length < 2)
            return null;

        var numbers = new int[Math.Min(parts.Length, 4)];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
    }
}
