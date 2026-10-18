using System.Diagnostics;
using Fuse.Graph;
using Fuse.Testing.Model;

namespace Fuse.Testing;

/// <summary>
///     The filter arguments a selection passes to a Microsoft.Testing.Platform run of xUnit v3 or TUnit, in the
///     framework's own spelling. MSTest and NUnit accept the VSTest filter on the platform, so they keep
///     <see cref="TestFilter"/> and never reach this. The arguments are bounded as the VSTest filter is: past the
///     bound, methods collapse to classes, then to no filter, so a run can hold more tests than the selection names
///     but never fewer.
/// </summary>
/// <remarks>
///     xUnit v3 takes one kind of simple filter only: methods and classes do not mix, so a selection with a class
///     prefix in it widens every method to its class. It spells the options with two dashes under
///     <c>dotnet test</c> on the platform and with one dash in its own application, so the spelling follows how the
///     client will run it. TUnit takes one <c>--treenode-filter</c>, so several classes collapse into one expression
///     that names them by their simple names, which can also hold a like-named class in another namespace.
/// </remarks>
internal static class PlatformFilter
{
    /// <summary>The most names a platform filter holds, which keeps the command line short.</summary>
    public const int MaxNames = 200;

    /// <summary>The arguments for <paramref name="selection"/> on <paramref name="framework"/>; empty runs every test in the project.</summary>
    /// <param name="framework">xUnit v3 or TUnit; a framework that takes the VSTest filter never reaches this.</param>
    /// <param name="selection">The tests chosen in the project.</param>
    /// <param name="runner">
    ///     True when <c>dotnet test</c> runs the platform (the repository's <c>global.json</c> opt-in), which decides
    ///     how xUnit v3 spells its options.
    /// </param>
    public static (string[] Arguments, FilterCollapse Collapse) For(TestFramework framework, TestSelection selection, bool runner) =>
        selection switch
        {
            TestSelection.Whole => ([], FilterCollapse.None),
            TestSelection.Methods methods => framework switch
            {
                TestFramework.XUnit => ForXUnit(methods, runner),
                TestFramework.TUnit => ForTUnit(methods),
                _ => throw new UnreachableException($"{framework} takes the VSTest filter, not platform arguments"),
            },
            _ => throw new UnreachableException($"a selection is whole or methods, not {selection.GetType().Name}"),
        };

    private static (string[] Arguments, FilterCollapse Collapse) ForXUnit(TestSelection.Methods methods, bool runner)
    {
        var prefixes = methods.Patterns.Where(p => p.EndsWith('.')).ToList();
        var names = methods.Patterns.Where(p => !p.EndsWith('.')).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (prefixes.Count == 0 && names.Count <= MaxNames)
            return ([.. names.SelectMany(n => new[] { runner ? "--filter-method" : "-method", n })], FilterCollapse.None);

        var classes = methods.Patterns.Select(ClassOf).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (classes.Count > MaxNames)
            return ([], FilterCollapse.ToWholeProject);
        var widened = names.Count > 0;
        return ([.. classes.SelectMany(c => new[] { runner ? "--filter-class" : "-class", c })],
            widened ? FilterCollapse.ToClasses : FilterCollapse.None);
    }

    private static (string[] Arguments, FilterCollapse Collapse) ForTUnit(TestSelection.Methods methods)
    {
        var entries = methods.Patterns
            .Select(p => (Class: Segments(ClassOf(p)), Method: p.EndsWith('.') ? null : p[(p.LastIndexOf('.') + 1)..]))
            .ToList();
        var classes = entries.Select(e => e.Class).Distinct().OrderBy(c => c.Namespace + "/" + c.Name, StringComparer.Ordinal).ToList();
        if (classes is [var only])
        {
            if (entries.All(e => e.Method is not null) && entries.Count <= MaxNames)
            {
                var chosen = entries.Select(e => e.Method!).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
                var method = chosen is [var one] ? one : $"({string.Join("|", chosen)})";
                return (["--treenode-filter", $"/*/{only.Namespace}/{only.Name}/{method}"], FilterCollapse.None);
            }

            return (["--treenode-filter", $"/*/{only.Namespace}/{only.Name}/*"],
                entries.Any(e => e.Method is not null) ? FilterCollapse.ToClasses : FilterCollapse.None);
        }

        if (classes.Count > MaxNames)
            return ([], FilterCollapse.ToWholeProject);
        var names = classes.Select(c => c.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        return (["--treenode-filter", $"/*/*/({string.Join("|", names)})/*"], FilterCollapse.ToClasses);
    }

    /// <summary>The class a selection pattern names: itself without its trailing dot, or a method without its last segment.</summary>
    private static string ClassOf(string pattern) =>
        pattern.EndsWith('.') ? pattern[..^1] : pattern[..pattern.LastIndexOf('.')];

    /// <summary>
    ///     A class fully qualified name as the segments a treenode filter compares: its namespace, and its simple name,
    ///     which for a nested class is the innermost name.
    /// </summary>
    private static (string Namespace, string Name) Segments(string className)
    {
        var nested = className.LastIndexOf('+');
        var simple = nested >= 0 ? className[(nested + 1)..] : className[(className.LastIndexOf('.') + 1)..];
        var outer = nested >= 0 ? className[..nested] : className;
        var lastDot = outer.LastIndexOf('.');
        return (lastDot > 0 ? outer[..lastDot] : "*", simple);
    }
}
