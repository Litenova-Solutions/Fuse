using System.Xml.Linq;
namespace Fuse.Testing.Selection;

/// <summary>
///     The centrally managed packages whose versions changed between HEAD and disk, read from the
///     <c>PackageVersion</c> items of <c>Directory.Packages.props</c>. A comment or whitespace edit reads the same
///     map, so it changes nothing.
/// </summary>
internal static class PackageVersionChanges
{
    /// <summary>
    ///     The package names added, removed or changed in version between <paramref name="headText"/> and
    ///     <paramref name="diskText"/>, or null when a version map cannot be read. Null selects every test project,
    ///     since the versions it would compare are unknown. Either text null means that side has no file.
    /// </summary>
    public static IReadOnlyList<string>? ChangedPackages(string? headText, string? diskText)
    {
        if (string.Equals(headText, diskText, StringComparison.Ordinal))
            return [];

        Dictionary<string, string> head;
        Dictionary<string, string> disk;
        try
        {
            head = ReadVersions(headText);
            disk = ReadVersions(diskText);
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException)
        {
            return null;
        }

        var changed = new List<string>();
        foreach (var name in head.Keys.Concat(disk.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = head.GetValueOrDefault(name);
            var after = disk.GetValueOrDefault(name);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changed.Add(name);
        }

        return changed;
    }

    /// <summary>The package versions <paramref name="text"/> names, empty when it has no file.</summary>
    private static Dictionary<string, string> ReadVersions(string? text)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text is null)
            return versions;

        var document = XDocument.Parse(text, LoadOptions.None);
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var name = element.Attribute("Include")?.Value.Trim() ?? element.Attribute("Update")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            versions[name] = element.Attribute("Version")?.Value.Trim() ?? "";
        }

        return versions;
    }
}
