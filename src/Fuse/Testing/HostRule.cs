using Fuse.Graph;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Recognizes code that an application host or a framework calls, rather than code in the repository. Such code has
///     no caller in source, so neither walk can follow it to a test by name. When a change reaches it in an application,
///     every test project that depends on the application is selected whole, because an integration test reaches it
///     through HTTP, a mediator or the host.
/// </summary>
internal static class HostRule
{
    /// <summary>
    ///     The test projects that can run <paramref name="project"/>'s code without naming it: the project itself when it
    ///     is a test project, and every test project that depends on it, directly or transitively.
    /// </summary>
    public static IEnumerable<ProjectNode> DependentTestProjects(RepoGraph graph, ProjectNode project)
    {
        if (project.IsTest)
            yield return project;
        foreach (var dependent in graph.DependentsOf(project).Where(d => d.IsTest))
            yield return dependent;
    }

    /// <summary>
    ///     True when an application host calls <paramref name="symbol"/>: an entry point in any project, or a member a
    ///     framework calls in an application. A framework-called member of a library is reached through its type instead.
    /// </summary>
    public static bool IsCalledByHost(ISymbol symbol, ProjectNode project) =>
        (symbol is IMethodSymbol method && IsEntryPoint(method)) || (project.IsExecutable && IsFrameworkInvoked(symbol));

    /// <summary>
    ///     True when a framework, not source code, is expected to call the symbol: it overrides or implements a member
    ///     declared outside the repository, its type derives from a non-trivial external base class, it carries
    ///     attributes, or it is an entry point.
    /// </summary>
    public static bool IsFrameworkInvoked(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method && IsEntryPoint(method))
            return true;
        if (symbol.DeclaredAccessibility is Accessibility.Private)
            return false;
        if (symbol is IMethodSymbol { OverriddenMethod: { } overridden } && !overridden.Locations.Any(l => l.IsInSource))
            return true;
        if (symbol.ContainingType is { } type)
        {
            if (type.AllInterfaces.SelectMany(i => i.GetMembers()).Any(m => !m.Locations.Any(l => l.IsInSource) && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol)))
                return true;
            for (var b = type.BaseType; b is not null; b = b.BaseType)
            {
                if (b.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)
                    break;
                if (!b.Locations.Any(l => l.IsInSource))
                    return true;
            }
        }

        var attributed = symbol.GetAttributes().Concat(symbol.ContainingType?.GetAttributes() ?? []);
        return attributed.Any(a => a.AttributeClass is { } c && !IsInertAttribute(c));
    }

    /// <summary>A <c>Main</c> method, or the method the compiler synthesizes for top-level statements (<c>&lt;Main&gt;$</c>).</summary>
    public static bool IsEntryPoint(IMethodSymbol method) =>
        method.IsStatic && method.Name is "Main" or WellKnownMemberNames.TopLevelStatementsEntryPointMethodName;

    /// <summary>
    ///     An attribute that does not make a framework call the member it is on: the compiler, debugger and analyzer
    ///     attributes, and <c>Obsolete</c>, <c>Serializable</c> and <c>Flags</c>.
    /// </summary>
    private static bool IsInertAttribute(INamedTypeSymbol attribute) =>
        attribute.ContainingNamespace?.ToDisplayString() is "System.Runtime.CompilerServices" or "System.Diagnostics" or "System.Diagnostics.CodeAnalysis"
        || attribute.Name is "ObsoleteAttribute" or "SerializableAttribute" or "FlagsAttribute";
}
