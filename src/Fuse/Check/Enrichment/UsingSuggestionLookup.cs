using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Check.Enrichment;

/// <summary>
///     Finds the using directives a repair packet names for a missing type: the namespaces that declare
///     the type, exact arity first, then outside System, then by name.
/// </summary>
internal static class UsingSuggestionLookup
{
    /// <summary>
    ///     Finds the namespaces that declare the missing type in <paramref name="diagnostic"/>, as using
    ///     directives, searching the compilation first and the other projects of <paramref name="solution"/> next.
    /// </summary>
    internal static string[] MissingTypeUsings(
        Diagnostic diagnostic,
        SemanticModel model,
        Compilation compilation,
        ISymbol within,
        SyntaxToken token,
        Solution? solution,
        CancellationToken cancellationToken)
    {
        var simple = token.Parent as SimpleNameSyntax
            ?? token.Parent?.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
        var typeName = simple?.Identifier.ValueText ?? RepairPackets.FirstQuotedTerm(diagnostic.GetMessage(CultureInfo.InvariantCulture));
        if (string.IsNullOrEmpty(typeName))
            return [];

        var arity = simple is GenericNameSyntax generic ? generic.Arity : 0;
        var isAttribute = simple is not null && IsInAttribute(simple);
        var searchNames = isAttribute && !typeName.EndsWith("Attribute", StringComparison.Ordinal)
            ? new[] { typeName, typeName + "Attribute" }
            : [typeName];

        var candidates = new List<(int ArityPenalty, int SystemPenalty, string Namespace, string Directive)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void CollectFromCompilation(Compilation comp, bool checkAccessibility)
        {
            var queue = new Queue<INamespaceSymbol>();
            queue.Enqueue(comp.GlobalNamespace);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ns = queue.Dequeue();
                foreach (var childNs in ns.GetNamespaceMembers())
                    queue.Enqueue(childNs);

                if (ns.IsGlobalNamespace)
                    continue;

                foreach (var name in searchNames)
                {
                    foreach (var type in ns.GetTypeMembers(name))
                    {
                        if (checkAccessibility && !comp.IsSymbolAccessibleWithin(type, within))
                            continue;
                        if (!checkAccessibility && type.DeclaredAccessibility != Accessibility.Public)
                            continue;
                        var nsName = ns.ToDisplayString();
                        var directive = $"using {nsName};";
                        if (!seen.Add(directive))
                            continue;
                        var arityPenalty = arity == 0 ? (type.Arity == 0 ? 0 : 1) : (type.Arity == arity ? 0 : 1);
                        var systemPenalty = nsName == "System" || nsName.StartsWith("System.", StringComparison.Ordinal) ? 1 : 0;
                        candidates.Add((arityPenalty, systemPenalty, nsName, directive));
                    }
                }
            }
        }

        CollectFromCompilation(compilation, checkAccessibility: true);

        if (solution is not null && candidates.Count == 0)
        {
            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (project.AssemblyName == compilation.AssemblyName)
                    continue;
                if (project.TryGetCompilation(out var otherComp) && otherComp is not null)
                    CollectFromCompilation(otherComp, checkAccessibility: false);
            }
        }

        return candidates
            .OrderBy(c => c.ArityPenalty)
            .ThenBy(c => c.SystemPenalty)
            .ThenBy(c => c.Namespace, StringComparer.Ordinal)
            .Take(RepairPackets.MaxSuggestions)
            .Select(c => c.Directive)
            .ToArray();
    }

    private static bool IsInAttribute(SimpleNameSyntax simple) =>
        simple.Parent is AttributeSyntax
        || (simple.Parent is QualifiedNameSyntax q && q.Parent is AttributeSyntax)
        || (simple.Parent is AliasQualifiedNameSyntax a && a.Parent is AttributeSyntax);
}
