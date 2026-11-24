using System.Collections.Concurrent;
using Fuse.Paths;

namespace Fuse.Dotnet;

/// <summary>
///     Detects the active .NET SDK version for a repository and exposes feature flags for .NET 11+ build and
///     Microsoft.Testing.Platform capabilities, plus MSBuild server shutdown hygiene. Results are cached per
///     repository root and invalidated if <c>global.json</c> changes.
/// </summary>
internal sealed record SdkCapabilities(string RawVersion, int Major, int Minor)
{
    private static readonly ConcurrentDictionary<string, CachedCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback capabilities when <c>dotnet --version</c> cannot be probed.</summary>
    public static SdkCapabilities Unknown { get; } = new("0.0.0", 0, 0);

    /// <summary>True when the active SDK supports multithreaded MSBuild (<c>-mt</c>, .NET 11+).</summary>
    public bool SupportsMultithreadedBuild => Major >= 11;

    /// <summary>True when <c>dotnet test</c> supports <c>--maximum-failed-tests</c> (.NET 11+).</summary>
    public bool SupportsMtpMaxFailedTests => Major >= 11;

    /// <summary>True when <c>dotnet test</c> supports <c>--collect-test-map</c> and <c>--affected-tests</c> (.NET 11+).</summary>
    public bool SupportsMtpAffectedTests => Major >= 11;

    /// <summary>Returns cached SDK capabilities for <paramref name="root"/>, probing <c>dotnet --version</c> on first use.</summary>
    public static SdkCapabilities For(RepoRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return For(root.Path);
    }

    /// <summary>Returns cached SDK capabilities for <paramref name="workingDirectory"/>, probing <c>dotnet --version</c> on first use.</summary>
    public static SdkCapabilities For(string workingDirectory)
    {
        var key = CacheKey(workingDirectory);
        var stamp = GlobalJsonStamp(key);
        if (Cache.TryGetValue(key, out var cached) && cached.GlobalJsonStamp == stamp)
            return cached.Capabilities;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SdkCapabilities detected;
        try
        {
            detected = Task.Run(() => ProbeAsync(key, timeout.Token), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return Unknown;
        }
        if (detected.Major > 0)
            Cache[key] = new CachedCapabilities(stamp, detected);
        return detected;
    }

    /// <summary>Detects and caches SDK capabilities for <paramref name="workingDirectory"/> asynchronously.</summary>
    public static async Task<SdkCapabilities> DetectAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(workingDirectory);
        var stamp = GlobalJsonStamp(key);
        if (Cache.TryGetValue(key, out var cached) && cached.GlobalJsonStamp == stamp)
            return cached.Capabilities;

        var detected = await ProbeAsync(key, cancellationToken).ConfigureAwait(false);
        if (detected.Major > 0)
            Cache[key] = new CachedCapabilities(stamp, detected);
        return detected;
    }

    /// <summary>Parses a <c>dotnet --version</c> output string such as <c>11.0.100</c> or <c>11.0.100-preview.1</c>.</summary>
    public static SdkCapabilities FromVersionString(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
            return Unknown;

        var line = versionOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line))
            return Unknown;

        var core = line.Split('-', 2)[0];
        var parts = core.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || major <= 0
            || minor < 0)
            return Unknown;

        return new SdkCapabilities(line, major, minor);
    }

    /// <summary>Shuts down the MSBuild build server (<c>dotnet build-server shutdown --msbuild</c>) to release idle memory.</summary>
    public static async Task<bool> ShutdownMsBuildServerAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var targetDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory;
        try
        {
            var result = await ProcessRunner.RunAsync("dotnet", ["build-server", "shutdown", "--msbuild"], targetDirectory, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Clears cached SDK probes; intended for tests.</summary>
    internal static void ClearCache() => Cache.Clear();

    private static async Task<SdkCapabilities> ProbeAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return Unknown;
        try
        {
            var result = await ProcessRunner.RunAsync("dotnet", ["--version"], directory, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? FromVersionString(result.Output) : Unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unknown;
        }
    }

    private static string CacheKey(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Environment.CurrentDirectory;
        var full = Path.GetFullPath(workingDirectory);
        var root = Directory.Exists(full) ? RepoRoot.Find(full)?.Path : null;
        return root ?? full;
    }

    private static (string Path, long Ticks) GlobalJsonStamp(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return ("", 0);
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "global.json");
                if (File.Exists(candidate))
                    return (candidate, File.GetLastWriteTimeUtc(candidate).Ticks);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ("", 0);
    }

    private sealed record CachedCapabilities((string Path, long Ticks) GlobalJsonStamp, SdkCapabilities Capabilities);
}
