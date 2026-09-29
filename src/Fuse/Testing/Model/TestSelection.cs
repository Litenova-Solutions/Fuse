using System.Collections.Immutable;

namespace Fuse.Testing.Model;

/// <summary>The tests chosen in one test project.</summary>
internal abstract record TestSelection
{
    private TestSelection()
    {
    }

    /// <summary>Every test in the project, because no narrower selection is known to include every affected test.</summary>
    /// <param name="Reason">
    ///     Why the project runs whole, written to follow "whole projects where" in the summary, for example "App uses the
    ///     changed code and runs behind an application host".
    /// </param>
    public sealed record Whole(string Reason) : TestSelection;

    /// <summary>The tests whose fully qualified name contains one of the patterns.</summary>
    /// <param name="Patterns">
    ///     Fully qualified method names (<c>Ns.Outer+Inner.Method</c>) or class prefixes (<c>Ns.Class.</c>), compared
    ///     ordinally. Immutable, so a selection a walk has handed out never changes under its reader.
    /// </param>
    public sealed record Methods(ImmutableHashSet<string> Patterns) : TestSelection;
}
