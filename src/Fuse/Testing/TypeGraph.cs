using Fuse.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>
///     A type-level dependency graph built from syntax alone: a type depends on every type (and extension-method
///     class) whose name appears in its files and that its project can see. Building it binds nothing, so it covers
///     thousands of files in about a second, and per-file results are cached by text version.
/// </summary>
/// <remarks>
///     Matching by name over-approximates (two types with one name both count), which only selects extra tests. Code
///     that uses a type without naming it still reaches it through the type that hands it out, because that type's
///     declaration names it. Extension methods are matched by method name, since callers name the method, not its class.
/// </remarks>
internal sealed class TypeGraph
{
    private readonly Dictionary<TypeKey, TypeEntry> _types = [];
    private readonly Dictionary<TypeKey, HashSet<TypeKey>> _dependents = [];

    private TypeGraph()
    {
    }

    /// <summary>A type (or, for a file without type declarations, the file itself) within one Roslyn project.</summary>
    internal readonly record struct TypeKey(ProjectId Project, string Name);

    /// <summary>What is known about a type in the graph.</summary>
    internal sealed record TypeEntry(TypeKey Key, ProjectNode? Node, string TestName);

    public IReadOnlyDictionary<TypeKey, TypeEntry> Types => _types;

    /// <summary>Builds the graph over <paramref name="projects"/>, reusing per-file results from <paramref name="cache"/>.</summary>
    public static async Task<TypeGraph> BuildAsync(
        IReadOnlyList<Project> projects,
        Func<Project, ProjectNode?> nodeOf,
        Dictionary<DocumentId, (VersionStamp Version, FileFacts Facts)> cache,
        CancellationToken cancellationToken)
    {
        var graph = new TypeGraph();
        var facts = new List<(Project Project, FileFacts Facts)>();
        foreach (var project in projects)
        {
            var node = nodeOf(project);
            foreach (var document in project.Documents)
            {
                var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                if (!cache.TryGetValue(document.Id, out var cached) || cached.Version != version)
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    cached = (version, FileFacts.From(root!, document.FilePath ?? document.Name));
                    cache[document.Id] = cached;
                }

                facts.Add((project, cached.Facts));
                foreach (var (name, testName) in cached.Facts.Declared)
                {
                    var key = new TypeKey(project.Id, name);
                    graph._types.TryAdd(key, new TypeEntry(key, node, testName));
                }
            }
        }

        // Which types each name can refer to, per project.
        var byName = new Dictionary<string, List<TypeKey>>(StringComparer.Ordinal);
        foreach (var (project, fileFacts) in facts)
        {
            foreach (var (name, _) in fileFacts.Declared)
                Add(byName, SimpleName(name), new TypeKey(project.Id, name));
            foreach (var (method, type) in fileFacts.ExtensionMethods)
                Add(byName, method, new TypeKey(project.Id, type));
        }

        var visible = projects.ToDictionary(p => p.Id, p => Visible(p));
        foreach (var (project, fileFacts) in facts)
        {
            var sees = visible[project.Id];
            foreach (var identifier in fileFacts.Identifiers)
            {
                if (!byName.TryGetValue(identifier, out var targets))
                    continue;
                foreach (var target in targets)
                {
                    if (!sees.Contains(target.Project))
                        continue;
                    foreach (var (source, _) in fileFacts.Declared)
                    {
                        var from = new TypeKey(project.Id, source);
                        if (from == target)
                            continue;
                        if (!graph._dependents.TryGetValue(target, out var dependents))
                            graph._dependents[target] = dependents = [];
                        dependents.Add(from);
                    }
                }
            }
        }

        return graph;
    }

    /// <summary>Every type that depends on one of <paramref name="start"/>, directly or transitively, including <paramref name="start"/>.</summary>
    public HashSet<TypeKey> ReverseClosure(IEnumerable<TypeKey> start)
    {
        var seen = new HashSet<TypeKey>();
        var queue = new Queue<TypeKey>();
        foreach (var key in start)
        {
            if (seen.Add(key))
                queue.Enqueue(key);
        }

        while (queue.Count > 0)
        {
            if (!_dependents.TryGetValue(queue.Dequeue(), out var dependents))
                continue;
            foreach (var dependent in dependents)
            {
                if (seen.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }

        return seen;
    }

    /// <summary>The key of the type declaring <paramref name="node"/>, or of the file when the node is outside any type.</summary>
    public static string DeclaringName(SyntaxNode node, string filePath)
    {
        var type = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()
                   ?? (SyntaxNode?)node.AncestorsAndSelf().OfType<DelegateDeclarationSyntax>().FirstOrDefault();
        return type is null ? FileKey(filePath) : QualifiedName(type);
    }

    private static HashSet<ProjectId> Visible(Project project)
    {
        var result = new HashSet<ProjectId>();
        var stack = new Stack<Project>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current.Id))
                continue;
            foreach (var reference in current.ProjectReferences)
            {
                if (current.Solution.GetProject(reference.ProjectId) is { } referenced)
                    stack.Push(referenced);
            }
        }

        return result;
    }

    private static void Add(Dictionary<string, List<TypeKey>> map, string name, TypeKey key)
    {
        if (!map.TryGetValue(name, out var list))
            map[name] = list = [];
        list.Add(key);
    }

    private static string SimpleName(string qualified)
    {
        var last = qualified.LastIndexOfAny(['.', '+']);
        return last < 0 ? qualified : qualified[(last + 1)..];
    }

    internal static string FileKey(string filePath) => "file:" + filePath;

    /// <summary>Namespace-qualified name with nested types joined by <c>+</c>, as test adapters report it.</summary>
    internal static string QualifiedName(SyntaxNode type)
    {
        var nesting = new Stack<string>();
        string? ns = null;
        for (var node = type; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax t:
                    nesting.Push(t.Identifier.Text);
                    break;
                case DelegateDeclarationSyntax d:
                    nesting.Push(d.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax n:
                    ns = ns is null ? n.Name.ToString() : n.Name + "." + ns;
                    break;
            }
        }

        return (ns is null ? "" : ns + ".") + string.Join("+", nesting);
    }

    /// <summary>The syntax facts of one file: types it declares, extension methods it declares, identifiers it mentions.</summary>
    internal sealed class FileFacts
    {
        public required List<(string Name, string TestName)> Declared { get; init; }

        public required List<(string Method, string Type)> ExtensionMethods { get; init; }

        public required HashSet<string> Identifiers { get; init; }

        public static FileFacts From(SyntaxNode root, string filePath)
        {
            var declared = new List<(string, string)>();
            var extensions = new List<(string, string)>();
            foreach (var node in root.DescendantNodes(n => n is not BlockSyntax))
            {
                if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                {
                    var name = QualifiedName(node);
                    declared.Add((name, name));
                }

                if (node is MethodDeclarationSyntax { ParameterList.Parameters: [var first, ..] } method
                    && first.Modifiers.Any(SyntaxKind.ThisKeyword)
                    && method.Parent is ClassDeclarationSyntax extensionClass)
                    extensions.Add((method.Identifier.Text, QualifiedName(extensionClass)));
            }

            // Top-level statements and assembly-level code belong to the file.
            if (declared.Count == 0 || root.ChildNodes().OfType<GlobalStatementSyntax>().Any())
                declared.Add((FileKey(filePath), FileKey(filePath)));

            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken))
                    identifiers.Add(token.ValueText);
            }

            return new FileFacts { Declared = declared, ExtensionMethods = extensions, Identifiers = identifiers };
        }
    }
}
