using System.Text;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds a deterministic, very compact sketch of a source file from its structural outline: the declared
///     types and the names of their members, with bodies and signatures omitted. Used for a file too large to
///     include even reduced, so it keeps presence and navigation in the output at a fraction of the tokens.
/// </summary>
/// <remarks>
///     The sketch is the outline rendered as one line per type (<c>kind Name: member, member</c>), capped so a
///     type with hundreds of members does not defeat the purpose. It carries no member bodies or string
///     literals, so it introduces no secret a later redaction pass would need to catch; it is still routed
///     through the redactor with every other rewritten entry to keep that invariant uniform.
/// </remarks>
public static class FileSketchBuilder
{
    // Members listed per type before the remainder is summarized as a count, so a giant type stays compact.
    private const int MaxMembersPerType = 40;

    /// <summary>
    ///     Renders the outline as a sketch.
    /// </summary>
    /// <param name="normalizedPath">The file's normalized relative path, named in the sketch header.</param>
    /// <param name="outline">The declared types and their members.</param>
    /// <returns>The sketch text, or an empty string when the outline declares no types.</returns>
    public static string Build(string normalizedPath, IReadOnlyList<OutlineSymbol> outline)
    {
        if (outline.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("// fuse:sketch ").Append(normalizedPath)
            .Append(" (oversized; structural outline only, bodies omitted)\n");

        foreach (var symbol in outline)
        {
            builder.Append(symbol.Kind).Append(' ').Append(symbol.Name);
            if (symbol.Members.Count > 0)
            {
                builder.Append(": ");
                var shown = Math.Min(symbol.Members.Count, MaxMembersPerType);
                for (var i = 0; i < shown; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(symbol.Members[i]);
                }

                if (symbol.Members.Count > shown)
                    builder.Append(", ... (").Append(symbol.Members.Count - shown).Append(" more)");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
