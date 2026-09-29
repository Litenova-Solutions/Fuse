using Fuse.Changes.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Changes;

/// <summary>
///     Whether the declarations a file exposes to other files changed: the check's question. A declaration changes when
///     its surface does, so an edit inside a body, a non-constant field's initializer, formatting or a comment changes
///     nothing another file can see, and the check stays within the edited files.
/// </summary>
internal static class SurfaceDiff
{
    /// <summary>The declaration changes from <paramref name="before"/>, the file at HEAD, to <paramref name="after"/>, the file on disk.</summary>
    public static FileChanges Compare(FileDeclarations before, FileDeclarations after)
    {
        var changes = new List<DeclarationChange>();
        var hasBroadChange = false;
        var keys = before.All.Concat(after.All).Where(d => d.Surface is not null).Select(d => d.Key).Distinct();
        foreach (var key in keys)
        {
            var old = before.Find(key);
            var now = after.Find(key);
            DeclarationChange? change = (old, now) switch
            {
                (null, { } added) => new DeclarationChange.Added(key, added.Names, DeclarationHeader.Of(added.Node)),
                ({ } removed, null) => new DeclarationChange.Removed(key, removed.Names, DeclarationHeader.Of(removed.Node)),
                ({ } previous, { } current) when previous.Surface != current.Surface =>
                    new DeclarationChange.Changed(key, current.Names, DeclarationHeader.Of(previous.Node), DeclarationHeader.Of(current.Node)),
                _ => null,
            };
            if (change is null)
                continue;
            changes.Add(change);
            hasBroadChange |= IsBroad(change, (now ?? old)!);
        }

        return new FileChanges(changes, hasBroadChange);
    }

    /// <summary>
    ///     True for a change that can break code that never names the declaration, so a reference search cannot bound
    ///     what it breaks: a global using or a file-level attribute list, which apply to every file of the project; a
    ///     delegate, which lambdas and method groups convert to without naming it; and a type whose header changed, which
    ///     can change conversions, overload resolution and inheritance wherever the type is used.
    /// </summary>
    private static bool IsBroad(DeclarationChange change, DeclarationNode declaration) =>
        change.Key is DeclarationKey.GlobalUsing or DeclarationKey.AssemblyAttribute
        || declaration.Node is DelegateDeclarationSyntax
        || change is DeclarationChange.Changed { Key: DeclarationKey.NamedType };
}
