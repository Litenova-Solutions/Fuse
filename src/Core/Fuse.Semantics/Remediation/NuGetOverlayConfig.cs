using System.Text;
using System.Xml.Linq;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     A NuGet package source (a feed) read from a repository's NuGet configuration.
/// </summary>
/// <param name="Key">The source key (for example <c>nuget.org</c>).</param>
/// <param name="Value">The source URL or path.</param>
public sealed record PackageSource(string Key, string Value);

/// <summary>
///     Generates the overlay NuGet configuration that fixes the NU1507 failure (Central Package Management with
///     multiple sources but no source mapping) without editing the repository (C1). The overlay redefines the
///     repository's sources and adds a <c>packageSourceMapping</c> that maps every package pattern to all of
///     those sources, which satisfies NU1507's requirement that a mapping exist while leaving the effective set
///     of sources restore can use unchanged. It is passed to restore explicitly (<c>--configfile</c>); it is
///     never written into the repository, per the C1 hard rule.
/// </summary>
public static class NuGetOverlayConfig
{
    private const string DefaultSourceKey = "nuget.org";
    private const string DefaultSourceValue = "https://api.nuget.org/v3/index.json";

    /// <summary>
    ///     Builds the overlay NuGet configuration XML for a set of sources.
    /// </summary>
    /// <param name="sources">The package sources to redefine and map; when empty, nuget.org is used.</param>
    /// <returns>The overlay <c>NuGet.config</c> content, ready to write to a temp file and pass to restore.</returns>
    public static string Build(IReadOnlyList<PackageSource> sources)
    {
        var effective = sources.Count == 0
            ? [new PackageSource(DefaultSourceKey, DefaultSourceValue)]
            : sources;

        var packageSources = new XElement("packageSources", new XElement("clear"));
        var mapping = new XElement("packageSourceMapping");
        foreach (var source in effective)
        {
            packageSources.Add(new XElement("add",
                new XAttribute("key", source.Key),
                new XAttribute("value", source.Value)));
            // Map every package pattern to this source: with each source carrying "*", restore may satisfy any
            // package from any source, so the mapping exists (NU1507 satisfied) without narrowing resolution.
            mapping.Add(new XElement("packageSource",
                new XAttribute("key", source.Key),
                new XElement("package", new XAttribute("pattern", "*"))));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("configuration", packageSources, mapping));

        var builder = new StringBuilder();
        using (var writer = System.Xml.XmlWriter.Create(builder, new System.Xml.XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
        }))
        {
            document.Save(writer);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Reads the package sources declared in the nearest <c>NuGet.config</c> at or above a directory.
    /// </summary>
    /// <param name="startDirectory">The directory to search from, walking up to the filesystem root.</param>
    /// <returns>
    ///     The declared sources (excluding entries removed by a <c>clear</c>), or nuget.org when no config or no
    ///     sources are found, so the overlay always has at least one usable source.
    /// </returns>
    public static IReadOnlyList<PackageSource> ReadSources(string startDirectory)
    {
        var configPath = FindNearestConfig(startDirectory);
        if (configPath is null)
            return [new PackageSource(DefaultSourceKey, DefaultSourceValue)];

        XDocument document;
        try
        {
            document = XDocument.Load(configPath);
        }
        catch (System.Xml.XmlException)
        {
            return [new PackageSource(DefaultSourceKey, DefaultSourceValue)];
        }
        catch (IOException)
        {
            return [new PackageSource(DefaultSourceKey, DefaultSourceValue)];
        }

        var packageSources = document.Root?
            .Elements("packageSources")
            .LastOrDefault();
        if (packageSources is null)
            return [new PackageSource(DefaultSourceKey, DefaultSourceValue)];

        var sources = new List<PackageSource>();
        foreach (var element in packageSources.Elements())
        {
            // A <clear/> resets any sources declared above it in this same file.
            if (element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase))
                sources.Clear();
            else if (element.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                var key = element.Attribute("key")?.Value;
                var value = element.Attribute("value")?.Value;
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    sources.Add(new PackageSource(key, value));
            }
        }

        return sources.Count == 0
            ? [new PackageSource(DefaultSourceKey, DefaultSourceValue)]
            : sources;
    }

    // NuGet.config is case-insensitively named; match either casing walking up from the start directory.
    private static string? FindNearestConfig(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            foreach (var name in new[] { "NuGet.config", "nuget.config" })
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
