using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Fuse.Testing.Model;

namespace Fuse.Testing;

/// <summary>
///     The VSTest filter a selection passes to <c>dotnet test</c>: any test whose fully qualified name contains one of its
///     patterns. The client writes it into a runsettings file, so its length does not depend on the command line, but its
///     number of patterns is bounded: a selection with more collapses to class prefixes, then to no filter, so it can run
///     more tests than the selection names but never fewer.
/// </summary>
internal static class TestFilter
{
    /// <summary>The most patterns a filter holds, far above the few hundred tests a large selection names.</summary>
    public const int MaxPatterns = 2000;

    /// <summary>The filter for <paramref name="selection"/>; its expression is null to run every test in the project.</summary>
    public static RunFilter For(TestSelection selection) => selection switch
    {
        TestSelection.Whole => new RunFilter(null, 0, FilterCollapse.None),
        TestSelection.Methods methods => For(methods.Patterns),
        _ => throw new UnreachableException($"a selection is whole or methods, not {selection.GetType().Name}"),
    };

    private static RunFilter For(ImmutableHashSet<string> patterns)
    {
        if (patterns.Count <= MaxPatterns)
            return new RunFilter(Join(patterns), patterns.Count, FilterCollapse.None);
        var classes = patterns.Select(p => p.EndsWith('.') ? p : p[..(p.LastIndexOf('.') + 1)]).Distinct().ToList();
        return classes.Count <= MaxPatterns
            ? new RunFilter(Join(classes), classes.Count, FilterCollapse.ToClasses)
            : new RunFilter(null, 0, FilterCollapse.ToWholeProject);
    }

    private static string Join(IEnumerable<string> patterns) =>
        string.Join("|", patterns.OrderBy(p => p, StringComparer.Ordinal).Select(p => "FullyQualifiedName~" + Escape(p)));

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
