using Fuse.Semantics;

namespace Fuse.Retrieval;

/// <summary>
///     The NuGet upgrade oracle (F3): given the referenced and target versions of a package as assembly paths, it
///     computes the public-API break set between them - the removed, signature-changed, and accessibility-reduced
///     members - so a package bump's risk is knowable before the lockfile changes. It reuses the metadata surface
///     extractor and the T2 <see cref="PublicApiDelta" />, so a break here is the same class of answer review and
///     impact give over source.
/// </summary>
/// <remarks>
///     Blind spots are named in every report, never hidden: reflection and dynamic usage do not appear in a
///     metadata diff, and repo call-site intersection is bounded by the reference graph's coverage of external
///     package types (the R5 edges are FK-safe to solution source types, so external-package call sites are not
///     tracked; the item's stated Fallback is to ship the API-delta half and say so). A missing target assembly
///     yields an abstention, which is how the offline case surfaces (the version is not in the local cache).
/// </remarks>
public static class PackageUpgradeOracle
{
    /// <summary>
    ///     Analyzes the public-API break set between two package assembly versions.
    /// </summary>
    /// <param name="packageId">The package id, for the report header.</param>
    /// <param name="referencedAssemblyPath">The assembly path of the currently referenced version.</param>
    /// <param name="targetAssemblyPath">The assembly path of the target (upgrade) version.</param>
    /// <returns>The upgrade report: the breaking and additive changes, or an abstention when a version is unavailable.</returns>
    public static PackageUpgradeReport Analyze(string packageId, string referencedAssemblyPath, string targetAssemblyPath)
    {
        var referenced = MetadataSurfaceExtractor.Extract(referencedAssemblyPath);
        if (referenced.Count == 0)
            return PackageUpgradeReport.Abstain(packageId, $"the referenced assembly '{referencedAssemblyPath}' could not be read (is the package restored?)");

        var target = MetadataSurfaceExtractor.Extract(targetAssemblyPath);
        if (target.Count == 0)
            return PackageUpgradeReport.Abstain(packageId, $"the target assembly '{targetAssemblyPath}' is not available locally; fetch the version or run online");

        var delta = PublicApiDelta.Compute(referenced, target);
        return new PackageUpgradeReport(
            packageId,
            Available: true,
            Reason: null,
            BreakingChanges: delta.Breaking,
            AdditiveChanges: delta.Changes.Where(c => !c.Breaking).ToList(),
            BlindSpots: "Blind spots not in this metadata diff: reflection/dynamic usage, and repo call-site intersection (the reference graph does not track external-package call sites).");
    }

    /// <summary>
    ///     Analyzes a bump between two cached package versions, resolving each version's assembly from the local
    ///     NuGet cache; abstains (the offline case) when a version is not cached.
    /// </summary>
    /// <param name="packageId">The package id (case-insensitive; the cache folder is lowercased).</param>
    /// <param name="fromVersion">The currently referenced version.</param>
    /// <param name="toVersion">The target (upgrade) version.</param>
    /// <returns>The upgrade report, or an abstention when a version's assembly is not in the local cache.</returns>
    public static PackageUpgradeReport AnalyzeCachedVersions(string packageId, string fromVersion, string toVersion)
    {
        var fromDll = ResolveCachedAssembly(packageId, fromVersion);
        if (fromDll is null)
            return PackageUpgradeReport.Abstain(packageId, $"version {fromVersion} of '{packageId}' is not in the local NuGet cache; restore it or run online");
        var toDll = ResolveCachedAssembly(packageId, toVersion);
        if (toDll is null)
            return PackageUpgradeReport.Abstain(packageId, $"version {toVersion} of '{packageId}' is not in the local NuGet cache; restore it or run online");
        return Analyze(packageId, fromDll, toDll);
    }

    /// <summary>
    ///     Resolves a package version's main assembly from the local NuGet cache, choosing the highest-versioned
    ///     target-framework folder under <c>lib</c> (the closest to a modern reference); returns null when absent.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="version">The package version.</param>
    /// <returns>The assembly path, or null when the version or its assembly is not cached.</returns>
    public static string? ResolveCachedAssembly(string packageId, string version)
    {
        foreach (var root in CacheRoots())
        {
            var versionDir = Path.Combine(root, packageId.ToLowerInvariant(), version);
            var lib = Path.Combine(versionDir, "lib");
            if (!Directory.Exists(lib))
                continue;

            // Prefer the DLL named after the package; else the first DLL, in the highest-sorted TFM folder.
            var tfmDirs = Directory.GetDirectories(lib).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
            foreach (var tfm in tfmDirs)
            {
                var named = Path.Combine(tfm, $"{packageId}.dll");
                if (File.Exists(named))
                    return named;
                var any = Directory.EnumerateFiles(tfm, "*.dll").FirstOrDefault();
                if (any is not null)
                    return any;
            }
        }

        return null;
    }

    private static IEnumerable<string> CacheRoots()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            yield return env;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (Directory.Exists(profile))
            yield return profile;
    }
}

/// <summary>The NuGet upgrade oracle's report for one package bump (F3).</summary>
/// <param name="PackageId">The package analyzed.</param>
/// <param name="Available">Whether both versions were available and the diff ran.</param>
/// <param name="Reason">The abstention reason when <see cref="Available" /> is false.</param>
/// <param name="BreakingChanges">The breaking public-API changes (removed, signature-changed, accessibility-reduced).</param>
/// <param name="AdditiveChanges">The additive public-API changes (new members).</param>
/// <param name="BlindSpots">The named blind spots this diff cannot see, surfaced in every available report.</param>
public sealed record PackageUpgradeReport(
    string PackageId,
    bool Available,
    string? Reason,
    IReadOnlyList<ApiChange> BreakingChanges,
    IReadOnlyList<ApiChange> AdditiveChanges,
    string? BlindSpots)
{
    /// <summary>Whether the upgrade removes or changes any public API (a bump that needs review).</summary>
    public bool HasBreaking => Available && BreakingChanges.Count > 0;

    /// <summary>Creates an abstention report (a version was unavailable).</summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="reason">The reason the diff could not run.</param>
    /// <returns>An unavailable report.</returns>
    public static PackageUpgradeReport Abstain(string packageId, string reason) =>
        new(packageId, Available: false, reason, [], [], null);
}
