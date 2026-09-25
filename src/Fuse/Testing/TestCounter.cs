using Fuse.Graph;
using Fuse.Repo;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>Counts test methods by parsing test project sources, so the scope line can say "38 of 2,914" without discovery.</summary>
internal sealed class TestCounter
{
    private readonly Dictionary<string, (DateTime Stamp, List<string> Tests)> _cache = new(ChangeTracker.PathComparer);

    /// <summary>Fully qualified names (<c>Ns.Outer+Inner.Method</c>) of the test methods in <paramref name="project"/>.</summary>
    public IReadOnlyList<string> TestsIn(ProjectNode project)
    {
        var result = new List<string>();
        foreach (var file in project.Sources.Where(s => s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(file))
                continue;
            var stamp = File.GetLastWriteTimeUtc(file);
            if (!_cache.TryGetValue(file, out var entry) || entry.Stamp != stamp)
            {
                entry = (stamp, Parse(file));
                _cache[file] = entry;
            }

            result.AddRange(entry.Tests);
        }

        return result;
    }

    /// <summary>How many of <paramref name="tests"/> a selection matches, estimated from test methods declared in source.</summary>
    public static int Count(IReadOnlyList<string> tests, ProjectSelection selection) =>
        selection.All ? tests.Count : tests.Count(t => selection.Patterns.Any(p => t.Contains(p, StringComparison.Ordinal)));

    private static List<string> Parse(string file)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        var result = new List<string>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(TestAttributes.LooksLikeTest))
        {
            var names = new Stack<string>();
            string? ns = null;
            for (var node = method.Parent; node is not null; node = node.Parent)
            {
                if (node is BaseTypeDeclarationSyntax type)
                    names.Push(type.Identifier.Text);
                else if (node is BaseNamespaceDeclarationSyntax namespaceDeclaration)
                    ns = ns is null ? namespaceDeclaration.Name.ToString() : namespaceDeclaration.Name + "." + ns;
            }

            result.Add((ns is null ? "" : ns + ".") + string.Join("+", names) + "." + method.Identifier.Text);
        }

        return result;
    }
}
