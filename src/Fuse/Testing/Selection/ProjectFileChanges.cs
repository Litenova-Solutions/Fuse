using System.Text;
using System.Text.Json;
using System.Xml.Linq;
namespace Fuse.Testing.Selection;

/// <summary>
///     Whether a project file changed in a way that can change a build: a semantic comparison that reads the same
///     for a comment or whitespace edit. A file present on one side only changed, and a file that cannot be read
///     counts as changed, since its content is unknown.
/// </summary>
internal static class ProjectFileChanges
{
    /// <summary>
    ///     True when the project file moved between <paramref name="headText"/> and <paramref name="diskText"/> in a
    ///     <c>PackageReference</c>, <c>ProjectReference</c> or <c>PropertyGroup</c> property. Either text null means
    ///     that side has no file.
    /// </summary>
    public static bool ProjectContentChanged(string? headText, string? diskText)
    {
        if (string.Equals(headText, diskText, StringComparison.Ordinal))
            return false;
        if (headText is null || diskText is null)
            return true;

        ProjectSnapshot head;
        ProjectSnapshot disk;
        try
        {
            head = ProjectSnapshot.Read(headText);
            disk = ProjectSnapshot.Read(diskText);
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException)
        {
            return true;
        }

        return !head.Equals(disk);
    }

    /// <summary>
    ///     True when a <c>Directory.Build.props</c> or <c>Directory.Build.targets</c> file moved between
    ///     <paramref name="headText"/> and <paramref name="diskText"/> past comments and whitespace.
    /// </summary>
    public static bool BuildContentChanged(string? headText, string? diskText)
    {
        if (string.Equals(headText, diskText, StringComparison.Ordinal))
            return false;
        if (headText is null || diskText is null)
            return true;

        try
        {
            return CanonicalXml(XDocument.Parse(headText, LoadOptions.None)) != CanonicalXml(XDocument.Parse(diskText, LoadOptions.None));
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>True when <c>global.json</c> moved between <paramref name="headText"/> and <paramref name="diskText"/> past whitespace.</summary>
    public static bool GlobalJsonChanged(string? headText, string? diskText)
    {
        if (string.Equals(headText, diskText, StringComparison.Ordinal))
            return false;
        if (headText is null || diskText is null)
            return true;

        try
        {
            return CanonicalJson(headText) != CanonicalJson(diskText);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>The project snapshot: the sorted item and property entries the selection compares.</summary>
    private sealed record ProjectSnapshot(List<string> Packages, List<string> References, List<string> Properties)
    {
        public static ProjectSnapshot Read(string text)
        {
            var document = XDocument.Parse(text, LoadOptions.None);
            var packages = new List<string>();
            var references = new List<string>();
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "PackageReference" || e.Name.LocalName == "ProjectReference"))
            {
                var entry = CanonicalItem(element);
                if (element.Name.LocalName == "PackageReference")
                    packages.Add(entry);
                else
                    references.Add(entry);
            }

            var properties = new List<string>();
            foreach (var group in document.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                foreach (var property in group.Elements())
                {
                    var condition = property.Attribute("Condition")?.Value.Trim() ?? "";
                    properties.Add(property.Name.LocalName + "=" + property.Value.Trim() + "[" + condition + "]");
                }
            }

            packages.Sort(StringComparer.Ordinal);
            references.Sort(StringComparer.Ordinal);
            properties.Sort(StringComparer.Ordinal);
            return new ProjectSnapshot(packages, references, properties);
        }

        private static string CanonicalItem(XElement element)
        {
            var attributes = element.Attributes()
                .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                .Select(a => a.Name.LocalName + "=" + a.Value.Trim());
            var children = element.Elements()
                .OrderBy(e => e.Name.LocalName, StringComparer.Ordinal)
                .Select(e => e.Name.LocalName + "=" + e.Value.Trim());
            return element.Name.LocalName + "(" + string.Join(";", attributes) + ")[" + string.Join(";", children) + "]";
        }

        public bool Equals(ProjectSnapshot? other) =>
            other is not null
            && Packages.SequenceEqual(other.Packages, StringComparer.Ordinal)
            && References.SequenceEqual(other.References, StringComparer.Ordinal)
            && Properties.SequenceEqual(other.Properties, StringComparer.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Packages.Count, References.Count, Properties.Count);
    }

    /// <summary>The canonical XML of <paramref name="document"/>, without comments and whitespace-only text.</summary>
    private static string CanonicalXml(XDocument document) => CanonicalElement(document.Root);

    private static string CanonicalElement(XElement? element)
    {
        if (element is null)
            return "";
        var attributes = element.Attributes()
            .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => a.Name.LocalName + "=" + a.Value.Trim());
        var children = new List<string>();
        foreach (var node in element.Nodes())
        {
            if (node is System.Xml.Linq.XComment)
                continue;
            if (node is XText text)
            {
                if (text.Value.Trim().Length > 0)
                    children.Add("text=" + text.Value.Trim());
                continue;
            }

            if (node is XElement child)
                children.Add(CanonicalElement(child));
        }

        return element.Name.LocalName + "(" + string.Join(";", attributes) + "){" + string.Join(",", children) + "}";
    }

    /// <summary>The canonical JSON of <paramref name="text"/>, with object properties sorted and no whitespace or comments.</summary>
    private static string CanonicalJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return CanonicalJsonElement(document.RootElement);
    }

    private static string CanonicalJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonEncoded(p.Name) + ":" + CanonicalJsonElement(p.Value));
                return "{" + string.Join(",", members) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJsonElement)) + "]";
            default:
                return element.GetRawText().Trim();
        }
    }

    /// <summary>The JSON string literal for <paramref name="name"/>.</summary>
    private static string JsonEncoded(string name)
    {
        var builder = new StringBuilder("\"", name.Length + 2);
        foreach (var c in name)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                        builder.Append("\\u" + ((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
