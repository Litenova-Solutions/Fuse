using Fuse.Semantics.Remediation;
using Xunit;

namespace Fuse.Semantics.Tests;

// C1: the overlay NuGet.config remedy for NU1507 (Central Package Management with multiple sources and no
// source mapping). The overlay redefines the sources and adds a packageSourceMapping so the mapping exists
// (NU1507 satisfied) without narrowing resolution; it is passed to restore explicitly and never written into
// the repository. This remedy installs nothing, so it is the one C1 remedy exercisable in a no-install
// environment.
public sealed class NuGetOverlayConfigTests
{
    [Fact]
    public void Build_emits_sources_and_a_wildcard_mapping_for_each()
    {
        var overlay = NuGetOverlayConfig.Build([
            new PackageSource("nuget.org", "https://api.nuget.org/v3/index.json"),
            new PackageSource("internal", "https://pkgs.example.com/v3/index.json"),
        ]);

        Assert.Contains("<packageSources>", overlay);
        Assert.Contains("<clear", overlay);
        Assert.Contains("key=\"nuget.org\"", overlay);
        Assert.Contains("key=\"internal\"", overlay);
        Assert.Contains("<packageSourceMapping>", overlay);
        // Each source gets a package pattern "*" so the mapping exists without narrowing which source supplies what.
        Assert.Equal(2, CountOccurrences(overlay, "pattern=\"*\""));
    }

    [Fact]
    public void Build_defaults_to_nuget_org_when_no_sources_are_supplied()
    {
        var overlay = NuGetOverlayConfig.Build([]);
        Assert.Contains("key=\"nuget.org\"", overlay);
        Assert.Contains("api.nuget.org", overlay);
    }

    [Fact]
    public void Build_declares_utf8_so_the_file_saved_as_utf8_is_valid_xml()
    {
        // Regression: writing the overlay through a plain StringBuilder made the declaration read encoding="utf-16",
        // while the file is saved as UTF-8 without a BOM, so NuGet rejected it ("no Unicode byte order mark. Cannot
        // switch to Unicode."). The declaration must read utf-8.
        var overlay = NuGetOverlayConfig.Build([new PackageSource("nuget.org", "https://api.nuget.org/v3/index.json")]);
        Assert.Contains("encoding=\"utf-8\"", overlay);
        Assert.DoesNotContain("utf-16", overlay);
    }

    [Fact]
    public void ReadSources_resolves_a_relative_local_folder_to_an_absolute_path()
    {
        // The overlay is written to a temp directory, so a relative folder source must be made absolute against the
        // original config's directory, else it would resolve against the temp location and point nowhere.
        var dir = Path.Combine(Path.GetTempPath(), "fuse-nuget-overlay-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "NuGet.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="local" value="./local-feed" />
                  </packageSources>
                </configuration>
                """);

            var sources = NuGetOverlayConfig.ReadSources(dir);
            var local = sources.Single(s => s.Key == "local");
            Assert.True(Path.IsPathRooted(local.Value), $"expected an absolute path, got '{local.Value}'");
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "local-feed")), local.Value);
            // A URL source is left verbatim.
            Assert.Equal("https://api.nuget.org/v3/index.json", sources.Single(s => s.Key == "nuget.org").Value);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadSources_reads_the_declared_sources_and_honors_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-nuget-overlay-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "NuGet.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="internal" value="https://pkgs.example.com/v3/index.json" />
                  </packageSources>
                </configuration>
                """);

            var sources = NuGetOverlayConfig.ReadSources(dir);
            Assert.Equal(2, sources.Count);
            Assert.Contains(sources, s => s.Key == "nuget.org");
            Assert.Contains(sources, s => s.Key == "internal");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ReadSources_never_returns_empty()
    {
        // ReadSources walks up to the filesystem root, so an ancestor NuGet.config could exist on some machines;
        // the guarantee under test is that the result is never empty (so the overlay always has a usable source),
        // with a valid entry per source. When nothing is found it defaults to nuget.org.
        var dir = Path.Combine(Path.GetTempPath(), "fuse-nuget-overlay-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sources = NuGetOverlayConfig.ReadSources(dir);
            Assert.NotEmpty(sources);
            Assert.All(sources, s =>
            {
                Assert.False(string.IsNullOrEmpty(s.Key));
                Assert.False(string.IsNullOrEmpty(s.Value));
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
