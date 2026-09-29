using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Architecture;

/// <summary>
///     Enforces the dependency rules in <c>docs/architecture.md</c>: every namespace under <c>src/Fuse</c> has a row,
///     uses only the namespaces its row allows, no namespace that runs in the client reaches Roslyn or MSBuild, and no
///     two namespaces depend on each other. The table here and the one on that page change together.
/// </summary>
/// <remarks>
///     A file's namespace comes from its <c>namespace</c> line. What it uses comes from its syntax: every <c>Fuse.X</c>,
///     <c>Microsoft.CodeAnalysis</c> and <c>Microsoft.Build</c> name in a <c>using</c> directive or a fully qualified
///     reference, plus every simple name of a type declared in an enclosing namespace, which C# resolves without a
///     <c>using</c> (a file in <c>Fuse.Engine.Client</c> sees the types of <c>Fuse.Engine</c>). Comments and strings are
///     not code, so a name in them is not a use.
/// </remarks>
public class NamespaceDependencyTests
{
    private const string Roslyn = "Microsoft.CodeAnalysis";
    private const string MsBuild = "Microsoft.Build";
    private const string Rule2 = "rule 2 of docs/architecture.md: no client namespace uses Microsoft.CodeAnalysis, Microsoft.Build, or a Fuse namespace that does";

    private static readonly string[] Foundation = ["Fuse.Paths", "Fuse.Failures", "Fuse.Telemetry"];
    private static readonly string[] Sources = ["Fuse.Repo", "Fuse.Graph", "Fuse.Dotnet"];

    /// <summary>The table under "Dependency rules": each namespace and the Fuse namespaces it may use. Null means any.</summary>
    private static readonly Dictionary<string, string[]?> MayUse = new(StringComparer.Ordinal)
    {
        ["Fuse.Paths"] = [],
        ["Fuse.Failures"] = [],
        ["Fuse.Telemetry"] = [],
        ["Fuse.Dotnet"] = [.. Foundation],
        ["Fuse.Repo"] = ["Fuse.Dotnet", .. Foundation],
        ["Fuse.Graph"] = ["Fuse.Repo", "Fuse.Dotnet", .. Foundation],
        ["Fuse.Workspace"] = [.. Sources, .. Foundation],
        ["Fuse.Changes"] = ["Fuse.Workspace", .. Sources, .. Foundation],
        ["Fuse.Check.Model"] = [.. Foundation],
        ["Fuse.Check"] = ["Fuse.Check.Model", "Fuse.Changes", "Fuse.Workspace", .. Sources, .. Foundation],
        ["Fuse.Testing"] = ["Fuse.Changes", "Fuse.Workspace", .. Sources, .. Foundation],
        ["Fuse.Engine"] = ["Fuse.Check", "Fuse.Check.Model", "Fuse.Testing", "Fuse.Changes", "Fuse.Workspace", .. Sources, .. Foundation, "Fuse.Protocol"],
        // Decision D11: the wire carries the check model's CompilerError unchanged.
        ["Fuse.Protocol"] = [.. Foundation, "Fuse.Check.Model"],
        ["Fuse.Engine.Client"] = ["Fuse.Protocol", "Fuse.Repo", "Fuse.Dotnet", .. Foundation],
        ["Fuse.Operations"] = ["Fuse.Engine.Client", "Fuse.Protocol", "Fuse.Repo", "Fuse.Dotnet", .. Foundation],
        ["Fuse.Harnesses"] = ["Fuse.Operations", "Fuse.Protocol", "Fuse.Repo", .. Foundation],
        ["Fuse.Hooks"] = ["Fuse.Harnesses", "Fuse.Operations", "Fuse.Protocol", "Fuse.Repo", .. Foundation],
        ["Fuse.Mcp"] = ["Fuse.Operations", "Fuse.Engine.Client", "Fuse.Protocol", "Fuse.Repo", .. Foundation],
        ["Fuse"] = null,
    };

    /// <summary>
    ///     The namespaces whose code runs in the short-lived client: the surfaces, the operations and the client, and the
    ///     layers the client shares with the engine, which include the check model because the wire carries its
    ///     <c>CompilerError</c>. A hook pays for every assembly it loads.
    /// </summary>
    private static readonly string[] ClientSide =
        ["Fuse.Hooks", "Fuse.Mcp", "Fuse.Harnesses", "Fuse.Operations", "Fuse.Engine.Client", "Fuse.Protocol", "Fuse.Check.Model", "Fuse.Repo", "Fuse.Dotnet", .. Foundation];

    /// <summary>The namespaces rule 2 names as using Roslyn or MSBuild, whether or not a file in them names it directly.</summary>
    private static readonly string[] EngineSide = ["Fuse.Graph", "Fuse.Workspace", "Fuse.Changes", "Fuse.Check", "Fuse.Testing", "Fuse.Engine"];

    /// <summary>
    ///     Uses that a row does not allow yet: the code as it is today, before later steps of the migration order in
    ///     docs/architecture.md. Each names the step that deletes it, and Every_transitional_entry_is_still_needed fails
    ///     once one is unused.
    /// </summary>
    private static readonly (string From, string To, string Step)[] TransitionalUses =
    [
        // TestPlanner returns Protocol.TestPlan until step 4 gives Testing its own result model.
        ("Fuse.Testing", "Fuse.Protocol", "step 4"),
    ];

    private static readonly Lazy<Scan> Code = new(Scan.Read);

    [Fact]
    public void Every_namespace_has_a_row()
    {
        Assert.NotEmpty(Code.Value.Files);
        var missing = Code.Value.Files
            .Where(f => !MayUse.ContainsKey(f.Namespace))
            .Select(f => $"{f.Path}: {f.Namespace} has no row in the dependency table of docs/architecture.md; decide its layer and add the row there and in this test")
            .ToList();
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    [Fact]
    public void Every_namespace_uses_only_what_its_row_allows()
    {
        var broken = new List<string>();
        foreach (var file in Code.Value.Files)
        {
            if (!MayUse.TryGetValue(file.Namespace, out var allowed) || allowed is null)
                continue;
            foreach (var used in file.Uses.Where(u => u.StartsWith("Fuse", StringComparison.Ordinal) && u != file.Namespace))
            {
                if (!allowed.Contains(used) && !IsTransitional(file.Namespace, used))
                    broken.Add($"{file.Path}: {file.Namespace} uses {used}, which its row in docs/architecture.md does not allow; {file.Namespace} may use {Describe(allowed)}");
            }
        }

        Assert.True(broken.Count == 0, string.Join('\n', broken));
    }

    [Fact]
    public void No_client_namespace_reaches_Roslyn_or_MSBuild()
    {
        var code = Code.Value;
        var broken = new List<string>();
        foreach (var start in ClientSide.Where(code.Namespaces.Contains))
        {
            // Breadth first, so the chain reported for each finding is the shortest one.
            var cameFrom = new Dictionary<string, string> { [start] = "" };
            var queue = new Queue<string>([start]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current != start && EngineSide.Contains(current))
                {
                    broken.Add($"{Chain(code, cameFrom, current)}: {current} runs only in the engine; {Rule2}");
                    continue;
                }

                foreach (var library in new[] { Roslyn, MsBuild })
                {
                    if (code.Edges.TryGetValue((current, library), out var file))
                        broken.Add($"{Chain(code, cameFrom, current)} -> {library} ({file}): {Rule2}");
                }

                foreach (var ((from, to), _) in code.Edges.Where(e => e.Key.From == current && e.Key.To.StartsWith("Fuse", StringComparison.Ordinal)))
                {
                    if (cameFrom.TryAdd(to, from))
                        queue.Enqueue(to);
                }
            }
        }

        Assert.True(broken.Count == 0, string.Join('\n', broken.Distinct()));
    }

    [Fact]
    public void No_two_namespaces_depend_on_each_other()
    {
        var code = Code.Value;
        var fuse = code.Edges.Keys.Where(e => e.To.StartsWith("Fuse", StringComparison.Ordinal)).ToList();
        var reach = code.Namespaces.ToDictionary(n => n, n => Reachable(fuse, n), StringComparer.Ordinal);
        var cycles = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in code.Namespaces.Order(StringComparer.Ordinal))
        {
            if (reported.Contains(name))
                continue;
            // The namespaces that reach this one and that this one reaches form one cycle.
            var cycle = code.Namespaces.Where(other => reach[name].Contains(other) && reach[other].Contains(name)).Order(StringComparer.Ordinal).ToList();
            if (cycle.Count < 2)
                continue;
            reported.UnionWith(cycle);
            var inside = fuse.Where(e => cycle.Contains(e.From) && cycle.Contains(e.To)).Select(e => $"{e.From} -> {e.To} ({code.Edges[e]})");
            cycles.Add($"{string.Join(", ", cycle)} depend on each other: {string.Join("; ", inside)}");
        }

        Assert.True(cycles.Count == 0, string.Join('\n', cycles));
    }

    [Fact]
    public void Every_transitional_entry_is_still_needed()
    {
        var code = Code.Value;
        var slack = new List<string>();
        foreach (var (from, to, step) in TransitionalUses)
        {
            if (!code.Edges.ContainsKey((from, to)))
                slack.Add($"{from} no longer uses {to}; delete the transitional use ({step})");
            else if (MayUse.GetValueOrDefault(from)?.Contains(to) != false)
                slack.Add($"the row of {from} already allows {to}; delete the transitional use ({step})");
        }

        Assert.True(slack.Count == 0, string.Join('\n', slack));
    }

    private static bool IsTransitional(string from, string to) =>
        TransitionalUses.Any(t => t.From == from && t.To == to);

    private static string Describe(string[] allowed) => allowed.Length == 0 ? "no Fuse namespace" : string.Join(", ", allowed);

    /// <summary>The path from the start of a walk to <paramref name="end"/>, each step with the file that makes it.</summary>
    private static string Chain(Scan code, Dictionary<string, string> cameFrom, string end)
    {
        var steps = new List<string>();
        var current = end;
        for (; cameFrom[current].Length > 0; current = cameFrom[current])
            steps.Add($"{current} ({code.Edges[(cameFrom[current], current)]})");
        steps.Add(current);
        steps.Reverse();
        return string.Join(" -> ", steps);
    }

    /// <summary>Every namespace <paramref name="start"/> reaches through one or more uses; it contains the start only through a cycle.</summary>
    private static HashSet<string> Reachable(List<(string From, string To)> edges, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges.Where(e => e.From == current))
            {
                if (seen.Add(edge.To))
                    queue.Enqueue(edge.To);
            }
        }

        return seen;
    }

    /// <summary>One source file: its repository-relative path, the namespace it declares, and what it uses.</summary>
    /// <param name="Namespace">The namespace on the file's <c>namespace</c> line.</param>
    /// <param name="Uses">The Fuse namespaces it uses, plus <c>Microsoft.CodeAnalysis</c> and <c>Microsoft.Build</c>.</param>
    private sealed record SourceFile(string Path, string Namespace, IReadOnlySet<string> Uses);

    /// <summary>Every source file under <c>src/Fuse</c>, and every use between namespaces with the first file that makes it.</summary>
    private sealed class Scan
    {
        private static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

        private Scan(List<SourceFile> files)
        {
            Files = files;
            foreach (var file in files)
            {
                foreach (var used in file.Uses.Where(u => u != file.Namespace))
                    Edges.TryAdd((file.Namespace, used), file.Path);
            }

            Namespaces = [.. files.Select(f => f.Namespace), .. Edges.Keys.Select(e => e.To).Where(t => t.StartsWith("Fuse", StringComparison.Ordinal))];
        }

        public IReadOnlyList<SourceFile> Files { get; }

        public Dictionary<(string From, string To), string> Edges { get; } = [];

        public HashSet<string> Namespaces { get; }

        public static Scan Read()
        {
            var repository = RepositoryRoot();
            var source = Path.Combine(repository, "src", "Fuse");
            var parsed = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(source, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
                .Order(StringComparer.Ordinal)
                .Select(f => (Path: Path.GetRelativePath(repository, f).Replace('\\', '/'), Root: CSharpSyntaxTree.ParseText(File.ReadAllText(f), Options).GetCompilationUnitRoot()))
                .ToList();
            var declared = parsed.GroupBy(p => NamespaceOf(p.Path, p.Root), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.SelectMany(p => TopLevelTypes(p.Root)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var known = declared.Keys.Concat(MayUse.Keys).ToHashSet(StringComparer.Ordinal);

            var files = new List<SourceFile>();
            foreach (var (path, root) in parsed)
            {
                var name = NamespaceOf(path, root);
                var uses = QualifiedNames(root).Select(n => Owner(n, known)).OfType<string>()
                    .Concat(EnclosingNamespaceUses(root, name, declared))
                    .ToHashSet(StringComparer.Ordinal);
                files.Add(new SourceFile(path, name, uses));
            }

            return new Scan(files);
        }

        private static string RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Fuse.slnx")))
                    return directory.FullName;
            }

            throw new InvalidOperationException($"no Fuse.slnx above {AppContext.BaseDirectory}");
        }

        private static string NamespaceOf(string path, CompilationUnitSyntax root) =>
            root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString()
            ?? throw new InvalidOperationException($"{path} declares no namespace; every file under src/Fuse declares one");

        private static IEnumerable<string> TopLevelTypes(CompilationUnitSyntax root) =>
            root.Members.OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Members).Select(m => m switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
                _ => null,
            }).OfType<string>();

        /// <summary>Every dotted name that starts with <c>Fuse</c> or <c>Microsoft</c>, in a using directive or in code.</summary>
        private static IEnumerable<string> QualifiedNames(CompilationUnitSyntax root)
        {
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText is not ("Fuse" or "Microsoft") || IsRightOfDot(identifier) || IsNamespaceDeclaration(identifier))
                    continue;

                SyntaxNode node = identifier.Parent is AliasQualifiedNameSyntax alias && alias.Name == identifier ? alias : identifier;
                var parts = new List<string> { identifier.Identifier.ValueText };
                while (true)
                {
                    if (node.Parent is QualifiedNameSyntax qualified && qualified.Left == node)
                    {
                        parts.Add(qualified.Right.Identifier.ValueText);
                        node = qualified;
                    }
                    else if (node.Parent is MemberAccessExpressionSyntax access && access.Expression == node)
                    {
                        parts.Add(access.Name.Identifier.ValueText);
                        node = access;
                    }
                    else
                    {
                        break;
                    }
                }

                yield return string.Join('.', parts);
            }
        }

        /// <summary>
        ///     The enclosing namespaces a file uses by the simple name of one of their types, which C# resolves without a
        ///     <c>using</c>. A type of the file's own namespace with the same name hides the enclosing one.
        /// </summary>
        private static IEnumerable<string> EnclosingNamespaceUses(CompilationUnitSyntax root, string name, Dictionary<string, HashSet<string>> declared)
        {
            var own = declared.GetValueOrDefault(name) ?? [];
            var enclosing = new List<string>();
            for (var dot = name.LastIndexOf('.'); dot > 0; dot = name.LastIndexOf('.', dot - 1))
                enclosing.Add(name[..dot]);

            foreach (var simple in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var text = simple.Identifier.ValueText;
                if (IsRightOfDot(simple) || IsNamespaceDeclaration(simple) || own.Contains(text))
                    continue;
                var owner = enclosing.FirstOrDefault(e => declared.TryGetValue(e, out var types) && types.Contains(text));
                if (owner is not null)
                    yield return owner;
            }
        }

        /// <summary>
        ///     The namespace a dotted name belongs to: the longest known Fuse namespace it starts with, or Roslyn or MSBuild.
        ///     Null for any other name.
        /// </summary>
        private static string? Owner(string dotted, HashSet<string> known)
        {
            foreach (var library in new[] { Roslyn, MsBuild })
            {
                if (dotted == library || dotted.StartsWith(library + ".", StringComparison.Ordinal))
                    return library;
            }

            if (dotted != "Fuse" && !dotted.StartsWith("Fuse.", StringComparison.Ordinal))
                return null;
            for (var candidate = dotted; ; candidate = candidate[..candidate.LastIndexOf('.')])
            {
                if (known.Contains(candidate) || !candidate.Contains('.'))
                    return candidate;
            }
        }

        private static bool IsRightOfDot(SimpleNameSyntax name) =>
            name.Parent switch
            {
                QualifiedNameSyntax qualified => qualified.Right == name,
                MemberAccessExpressionSyntax access => access.Name == name,
                MemberBindingExpressionSyntax => true,
                AliasQualifiedNameSyntax alias => alias.Name == name && alias.Alias.Identifier.ValueText != "global",
                _ => false,
            };

        private static bool IsNamespaceDeclaration(SyntaxNode node) =>
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.Span.Contains(node.Span));
    }
}
