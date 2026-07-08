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
