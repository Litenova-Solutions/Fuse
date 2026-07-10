using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

// G4: the merged (version-2) bundle layout. WriteMerged assembles a bundle from per-project compiler-log
// fragments under fragments/, and CompilerLogPaths resolves the log(s) an oracle check iterates - the fragments
// for a merged bundle, or the single capture.complog for a direct (version-1) bundle.
public sealed class CaptureBundleV2Tests
{
    private static CaptureResult Graph() => CaptureResult.Ok(
        [new CapturedProject("App", "/repo/App.csproj", "App", ErrorCount: 0, TypeCount: 2, SymbolCount: 3, NodeCount: 1, EdgeCount: 0)]);

    [Fact]
    public void WriteMerged_produces_a_v2_bundle_and_CompilerLogPaths_returns_the_fragments()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-bundle-v2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Two stand-in fragment complogs (content is irrelevant to the layout logic under test).
            var fragA = Path.Combine(work, "a.complog");
            var fragB = Path.Combine(work, "b.complog");
            File.WriteAllText(fragA, "A");
            File.WriteAllText(fragB, "B");

            var bundle = Path.Combine(work, "bundle");
            var manifest = CaptureBundleIo.WriteMerged(bundle, [fragA, fragB], Graph(), commit: "abc", capturedUtc: "2026-07-09T00:00:00Z");

            Assert.Equal(CaptureManifest.CurrentFormatVersion, manifest.BundleFormatVersion);
            Assert.True(manifest.IsCompatibleWithRunningBuild, manifest.IncompatibilityReason);

            // The fragments were moved into fragments/ and are what an oracle check iterates.
            var logs = CaptureBundleIo.CompilerLogPaths(bundle);
            Assert.Equal(2, logs.Count);
            Assert.All(logs, l => Assert.Equal(CaptureManifest.FragmentsDirName, Path.GetFileName(Path.GetDirectoryName(l))));
            Assert.False(File.Exists(fragA), "the fragment should have been moved into the bundle, not left behind");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CompilerLogPaths_falls_back_to_the_single_complog_for_a_v1_bundle()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-bundle-v1", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var complog = Path.Combine(work, "capture.complog");
            File.WriteAllText(complog, "single");
            var bundle = Path.Combine(work, "bundle");
            CaptureBundleIo.Write(bundle, complog, Graph(), commit: null, capturedUtc: null);

            var logs = CaptureBundleIo.CompilerLogPaths(bundle);
            var single = Assert.Single(logs);
            Assert.Equal(CaptureManifest.CompilerLogFileName, Path.GetFileName(single));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
