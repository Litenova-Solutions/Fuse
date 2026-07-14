using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R8: the committed profile-v42.json must exist, carry the required schema keys, and stay aligned with the
// product version in Directory.Build.props. Regenerate with `fuse eval profile-v42 --repo NodaTime`.
public sealed class ProfileV42ResultTests
{
    private static readonly string[] RequiredRootKeys =
    [
        "schemaVersion",
        "fuseVersion",
        "suite",
        "description",
        "generated",
        "placeholder",
        "repo",
        "indexMode",
        "fileCount",
        "symbolCount",
        "iterations",
        "operations",
        "hotspots",
        "notes"
    ];

    private static readonly string[] RequiredOperationKeys = ["localize", "findSymbol", "reviewPlan", "reconcile"];

    private static readonly string[] RequiredOperationMetricKeys = ["p50Ms", "p95Ms", "selfTimePercent", "sampleCount"];

    private static string? RepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fuse.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string ProfileResultPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "benchmarks", "results", "profile-v42.json");

    private static string ReadProductVersion(string repoRoot)
    {
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"Directory.Build.props not found at {propsPath}");
        var version = XDocument.Load(propsPath)
            .Descendants("Version")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        Assert.False(string.IsNullOrWhiteSpace(version), "Directory.Build.props must declare <Version>.");
        return version!;
    }

    [Fact]
    public void Fuse_eval_profile_v42_result_has_required_schema_and_matches_product_version()
    {
        var root = RepoRoot();
        Assert.NotNull(root);

        var path = ProfileResultPath(root!);
        Assert.True(File.Exists(path), $"profile artifact missing at {path}; run `fuse eval profile-v42 --repo NodaTime`.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = document.RootElement;

        foreach (var key in RequiredRootKeys)
            Assert.True(rootElement.TryGetProperty(key, out _), $"profile-v42.json missing required key '{key}'.");

        Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("profile-v42", rootElement.GetProperty("suite").GetString());

        var productVersion = ReadProductVersion(root!);
        Assert.Equal(productVersion, rootElement.GetProperty("fuseVersion").GetString());

        var operations = rootElement.GetProperty("operations");
        foreach (var operation in RequiredOperationKeys)
        {
            Assert.True(operations.TryGetProperty(operation, out var metrics), $"operations missing '{operation}'.");
            foreach (var metric in RequiredOperationMetricKeys)
                Assert.True(metrics.TryGetProperty(metric, out _), $"operations.{operation} missing '{metric}'.");
        }

        var hotspots = rootElement.GetProperty("hotspots");
        Assert.True(hotspots.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.Array);
        Assert.True(hotspots.TryGetProperty("roslyn", out var roslyn) && roslyn.ValueKind == JsonValueKind.Array);

        var notes = rootElement.GetProperty("notes");
        Assert.Equal(JsonValueKind.Array, notes.ValueKind);
        Assert.NotEmpty(notes.EnumerateArray());
    }
}
