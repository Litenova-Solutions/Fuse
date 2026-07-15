using System.Xml.Linq;
using Xunit;

namespace Fuse.Semantics.Tests;

// R11: Fuse.Semantics must not reference Fuse.Plugins.Languages.CSharp.Roslyn directly; the semantic tier
// reaches Roslyn only through host/plugin registration.
public sealed class ProjectReferenceTests
{
    [Fact]
    public void SemanticsProjectDoesNotReferenceRoslynPlugin()
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Core", "Fuse.Semantics", "Fuse.Semantics.csproj"));

        var document = XDocument.Load(projectPath);
        XNamespace msbuild = document.Root!.Name.Namespace;

        var references = document
            .Descendants(msbuild + "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();

        Assert.DoesNotContain(references, r =>
            r.Contains("Fuse.Plugins.Languages.CSharp.Roslyn", StringComparison.OrdinalIgnoreCase));
    }
}
