using System.Diagnostics;
using System.Text;
using Fuse.Testing.Model;

namespace Fuse.Testing;

/// <summary>
///     The VSTest filter a selection passes to <c>dotnet test</c>: any test whose fully qualified name contains one of its
///     patterns. A filter too long for the command line collapses to class prefixes, then to no filter, so it can run
///     more tests than the selection names but never fewer.
/// </summary>
internal static class TestFilter
{
    // Windows limits a command line to 32,767 characters; stay well under it with the rest of the arguments.
    private const int MaxLength = 8000;

    /// <summary>The filter for <paramref name="selection"/>, or null to run every test in the project.</summary>
    public static string? For(TestSelection selection) => selection switch
    {
        TestSelection.Whole => null,
        TestSelection.Methods methods => For(methods.Patterns),
        _ => throw new UnreachableException($"a selection is whole or methods, not {selection.GetType().Name}"),
    };

    private static string? For(IReadOnlyCollection<string> patterns)
    {
        var filter = Join(patterns);
        if (filter.Length <= MaxLength)
            return filter;
        var classes = patterns.Select(p => p.EndsWith('.') ? p : p[..(p.LastIndexOf('.') + 1)]).Distinct().ToList();
        filter = Join(classes);
        return filter.Length <= MaxLength ? filter : null;
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
