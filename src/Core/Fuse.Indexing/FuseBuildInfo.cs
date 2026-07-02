using System.Reflection;

namespace Fuse.Indexing;

/// <summary>
///     The running Fuse build's version, used to stamp an index and detect when it was written by an
///     incompatible Fuse.
/// </summary>
/// <remarks>
///     The version is read from the assembly's <see cref="AssemblyInformationalVersionAttribute" /> with any
///     source-control build metadata (the <c>+sha</c> suffix) trimmed, so it matches the product version
///     (for example <c>3.1.0</c>). All Fuse assemblies share one version, so any Fuse-owned assembly is a
///     valid source; this type lives in the indexing assembly because the index store stamps and compares it.
/// </remarks>
public static class FuseBuildInfo
{
    /// <summary>The running Fuse version, for example <c>3.1.0</c>, or <c>0.0.0</c> when unset.</summary>
    public static string Current { get; } = ReadInformationalVersion();

    /// <summary>
    ///     Whether an index stamped with <paramref name="storedVersion" /> is compatible with the running
    ///     build. Compatibility is by <c>major.minor</c>: a patch release keeps the extraction contract, while
    ///     a minor or major release may change what is extracted and so warrants a rebuild.
    /// </summary>
    /// <param name="storedVersion">The <c>fuse_version</c> stamped on the index, or null when absent.</param>
    /// <returns>
    ///     True when the stored version is null (unknown, treated as compatible so a pre-stamp index is not
    ///     wiped) or shares the running build's <c>major.minor</c>; false when the <c>major.minor</c> differs.
    /// </returns>
    public static bool IsCompatible(string? storedVersion)
    {
        if (string.IsNullOrWhiteSpace(storedVersion))
            return true;
        return MajorMinor(storedVersion) == MajorMinor(Current);
    }

    // Reduce a version string to its major.minor for the compatibility comparison; a shorter or unparseable
    // string is returned trimmed so two identical unparseable stamps still compare equal.
    private static string MajorMinor(string version)
    {
        var core = version.Trim();
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            core = core[..plus];
        var parts = core.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : core;
    }

    private static string ReadInformationalVersion()
    {
        var informational = typeof(FuseBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return typeof(FuseBuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // Trim the +sha build metadata so the stamp is the product version (3.1.0), not 3.1.0+abc1234.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
