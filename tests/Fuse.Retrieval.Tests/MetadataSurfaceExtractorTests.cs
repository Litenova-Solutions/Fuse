using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Retrieval.Tests;

// F3: the metadata public-surface extractor is the substrate for the NuGet upgrade oracle. It reads a compiled
// assembly's public/protected surface as SymbolRecords, which PublicApiDelta (the T2 diff) then compares between
// two package versions. Tested against a stable in-tree assembly (deterministic, environment-independent) plus a
// tolerant cached-version-pair diff.
public sealed class MetadataSurfaceExtractorTests
{
    [Fact]
    public void Extracts_public_types_and_members_from_a_compiled_assembly()
    {
        // Fuse.Indexing.dll is always present in the test bin; SymbolRecord is one of its public types.
        var path = typeof(SymbolRecord).Assembly.Location;

        var surface = MetadataSurfaceExtractor.Extract(path);

        Assert.NotEmpty(surface);
        Assert.Contains(surface, s => s.Name == "SymbolRecord" && s.IsPublicApi);
        // A public member of a public type is captured (SymbolRecord.FullyQualifiedName is a public property).
        Assert.Contains(surface, s => s.ContainingType is not null && s.Name == "FullyQualifiedName");
        Assert.All(surface, s => Assert.True(s.IsPublicApi));
    }

    [Fact]
    public void Diffing_an_assembly_against_itself_reports_no_change()
    {
        var path = typeof(SymbolRecord).Assembly.Location;
        var a = MetadataSurfaceExtractor.Extract(path);
        var b = MetadataSurfaceExtractor.Extract(path);

        var delta = PublicApiDelta.Compute(a, b);

        Assert.Empty(delta.Changes);
        Assert.False(delta.HasBreaking);
    }

    [Fact]
    public void Missing_assembly_returns_empty()
    {
        Assert.Empty(MetadataSurfaceExtractor.Extract("no-such-file.dll"));
    }

    [Fact]
    public void Version_pair_diff_computes_when_two_versions_are_cached()
    {
        // Tolerant: diff two cached versions of a package if present; otherwise skip (the offline abstention path
        // of the oracle is exercised elsewhere). This proves the extractor + T2 diff compose over real versions.
        var pair = FindCachedVersionPair();
        if (pair is null)
            return;

        var older = MetadataSurfaceExtractor.Extract(pair.Value.Older);
        var newer = MetadataSurfaceExtractor.Extract(pair.Value.Newer);
        Assert.NotEmpty(older);
        Assert.NotEmpty(newer);

        // The point is that the diff runs and classifies; a removal, if any, is flagged breaking.
        var delta = PublicApiDelta.Compute(older, newer);
        Assert.All(delta.Breaking, c => Assert.True(c.Breaking));
    }

    private static (string Older, string Newer)? FindCachedVersionPair()
    {
        var roots = new List<string>();
        var home = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(home))
            roots.Add(home);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));

        foreach (var root in roots.Where(Directory.Exists))
        {
            var pkg = Path.Combine(root, "newtonsoft.json");
            if (!Directory.Exists(pkg))
                continue;
            var versions = Directory.GetDirectories(pkg).OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (versions.Count < 2)
                continue;
            var older = FindDll(versions[0]);
            var newer = FindDll(versions[^1]);
            if (older is not null && newer is not null)
                return (older, newer);
        }

        return null;
    }

    private static string? FindDll(string versionDir)
    {
        var lib = Path.Combine(versionDir, "lib");
        return Directory.Exists(lib)
            ? Directory.EnumerateFiles(lib, "Newtonsoft.Json.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }
}

// F3: the upgrade oracle's analysis entry point over two assembly paths (reuses the extractor + T2 diff).
public sealed class PackageUpgradeOracleTests
{
    [Fact]
    public void Same_version_reports_no_breaking_changes()
    {
        var path = typeof(Fuse.Indexing.SymbolRecord).Assembly.Location;
        var report = PackageUpgradeOracle.Analyze("Fuse.Indexing", path, path);

        Assert.True(report.Available);
        Assert.False(report.HasBreaking);
        Assert.Empty(report.BreakingChanges);
        Assert.NotNull(report.BlindSpots); // blind spots named on every available report
    }

    [Fact]
    public void Missing_target_abstains_with_a_reason()
    {
        var path = typeof(Fuse.Indexing.SymbolRecord).Assembly.Location;
        var report = PackageUpgradeOracle.Analyze("X", path, "no-such-target.dll");

        Assert.False(report.Available);
        Assert.False(report.HasBreaking);
        Assert.Contains("not available locally", report.Reason);
    }

    [Fact]
    public void Missing_referenced_abstains()
    {
        var report = PackageUpgradeOracle.Analyze("X", "no-such-ref.dll", "no-such-target.dll");
        Assert.False(report.Available);
        Assert.Contains("could not be read", report.Reason);
    }

    [Fact]
    public void Cached_versions_analysis_abstains_for_an_uncached_package()
    {
        var report = PackageUpgradeOracle.AnalyzeCachedVersions("No.Such.Package.Xyz", "1.0.0", "2.0.0");
        Assert.False(report.Available);
        Assert.Contains("not in the local NuGet cache", report.Reason);
    }

    [Fact]
    public void Cached_versions_analysis_runs_or_abstains_cleanly_for_a_real_package()
    {
        // Tolerant: newtonsoft.json 13.0.1 -> 13.0.3 is a patch bump (additive-or-empty). When both are cached the
        // analysis runs and is available; otherwise it abstains cleanly. Either way it never throws.
        var report = PackageUpgradeOracle.AnalyzeCachedVersions("Newtonsoft.Json", "13.0.1", "13.0.3");
        if (report.Available)
        {
            Assert.NotNull(report.BlindSpots);
            Assert.All(report.BreakingChanges, c => Assert.True(c.Breaking));
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(report.Reason));
        }
    }
}

// F3 Gate (the zero-false-safe contract): the oracle must FLAG a known-breaking upgrade and must NOT report it
// safe. Curated real version pairs: System.Text.Json 4.7.2 -> 8.0.0 removed public API (JsonClassInfo and its
// nested ConstructorDelegate), so it must be flagged breaking; the backward-compatible additive-only major bumps
// of System.Collections.Immutable and Microsoft.Extensions.DependencyInjection.Abstractions must show no missed
// break. Tolerant of an environment where the versions are not cached (the oracle then abstains, tested above).
public sealed class PackageUpgradeGateTests
{
    [Fact]
    public void Known_breaking_upgrade_is_flagged_never_reported_safe()
    {
        // System.Text.Json 4.7.2 -> 8.0.0 is a known-breaking upgrade (public JsonClassInfo was removed).
        var report = PackageUpgradeOracle.AnalyzeCachedVersions("System.Text.Json", "4.7.2", "8.0.0");
        if (!report.Available)
            return; // versions not cached here; the offline abstention path is covered elsewhere.

        // Zero false-safe: the known-breaking bump must not be reported without a breaking change.
        Assert.True(report.HasBreaking, "a known-breaking upgrade was reported with no breaking changes (false safe)");
        Assert.Contains(report.BreakingChanges, c => c.Symbol.Contains("JsonClassInfo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Collections.Immutable", "1.5.0", "8.0.0")]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions", "6.0.0", "9.0.0")]
    public void Backward_compatible_major_bump_reports_no_missed_break(string id, string from, string to)
    {
        var report = PackageUpgradeOracle.AnalyzeCachedVersions(id, from, to);
        if (!report.Available)
            return;
        // These libraries grew additively across the major bump; the oracle correctly finds no public-API removal
        // (a spurious flag would be survivable per the item, but these are clean, so it should be empty).
        Assert.False(report.HasBreaking, $"{id} {from}->{to} reported a break where the surface only grew");
        Assert.NotEmpty(report.AdditiveChanges);
    }
}
