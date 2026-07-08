using System.Text.RegularExpressions;
using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     Turns a compiler diagnostic from a speculative typecheck (R1 <c>fuse_check</c>) into a repair packet
///     (R6): the fix context an agent needs so its next action after a failed check is not another round-trip to
///     go read the type. For a missing-member error it attaches the members the receiver type actually has, with
///     the nearest names first; for an unknown-type error it attaches the nearest type names in the index. The
///     packet is built from the persisted symbol table, so it costs one indexed read, not a re-analysis.
/// </summary>
/// <remarks>
///     Only the two diagnostics that dominate an agent's API-shape mistakes are handled (CS1061 member does not
///     exist, CS0246 type or namespace not found); any other diagnostic returns null, so the check output is
///     enriched only where a concrete suggestion is possible and is never padded with an empty packet.
/// </remarks>
public sealed partial class RepairPacketBuilder
{
    private const int MaxCandidates = 5;
    private const int MaxMembers = 30;
    private readonly IWorkspaceIndexStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RepairPacketBuilder" /> class.
    /// </summary>
    /// <param name="store">The workspace index store the packet's suggestions are drawn from.</param>
    public RepairPacketBuilder(IWorkspaceIndexStore store) => _store = store;

    /// <summary>
    ///     Builds a repair packet for a diagnostic, or returns null when no concrete suggestion is possible.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to explain.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The repair packet, or null when the diagnostic is not one the builder can enrich.</returns>
    public async Task<RepairPacket?> BuildAsync(CheckDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        return diagnostic.Id switch
        {
            // CS1061 (instance member access) and CS0117 (static/type member access) share the message shape
            // "'Type' does not contain a definition for 'Member'", so both list the receiver type's real members
            // with the nearest names first (S2 repair-packet expansion).
            "CS1061" or "CS0117" => await BuildMissingMemberAsync(diagnostic, cancellationToken),
            "CS0246" => await BuildUnknownTypeAsync(diagnostic, cancellationToken),
            _ => null,
        };
    }

    // CS1061: "'Shop.Order' does not contain a definition for 'GrandTotal'". The first quoted token is the
    // receiver type, the second is the member that does not exist; suggest the type's nearest-named real members.
    private async Task<RepairPacket?> BuildMissingMemberAsync(CheckDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        var quoted = QuotedTokens(diagnostic.Message);
        if (quoted.Count < 2)
            return null;
        var type = quoted[0];
        var missing = quoted[1];

        var members = await _store.GetMembersOfTypeAsync(type, MaxMembers, cancellationToken);
        if (members.Count == 0)
            return new RepairPacket(diagnostic.Id,
                $"'{type}' has no member '{missing}', and no members of '{type}' are recorded in the index (it may live in a referenced assembly, not in indexed source).",
                [], []);

        var candidates = members
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => LevenshteinDistance(n, missing))
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        return new RepairPacket(diagnostic.Id,
            $"'{type}' has no member '{missing}'. Its members are listed below; the nearest names are {string.Join(", ", candidates)}.",
            candidates,
            members);
    }

    // CS0246: "The type or namespace name 'Invoice' could not be found ...". The first quoted token is the
    // unknown type; suggest the nearest type names present in the index.
    private async Task<RepairPacket?> BuildUnknownTypeAsync(CheckDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        var quoted = QuotedTokens(diagnostic.Message);
        if (quoted.Count < 1)
            return null;
        var name = quoted[0];

        // Match a leading fragment of the unknown name so a near-miss (Invoice vs InvoiceLine) surfaces; rank the
        // candidates by edit distance to the full unknown name.
        var fragment = name.Length <= 3 ? name : name[..3];
        var matches = await _store.FindSymbolsByNameAsync(fragment, 50, cancellationToken);
        var candidates = matches
            .Where(m => m.Kind is "class" or "interface" or "struct" or "record" or "enum" or "type" or "delegate")
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => LevenshteinDistance(n, name))
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        if (candidates.Count == 0)
            return new RepairPacket(diagnostic.Id,
                $"The type '{name}' is not in the index. Check the name, add a using directive, or run fuse_index if it is new.",
                [], []);

        return new RepairPacket(diagnostic.Id,
            $"The type '{name}' is not in the index. The nearest type names are {string.Join(", ", candidates)}.",
            candidates,
            []);
    }

    // The single-quoted tokens in a diagnostic message, in order. Compiler messages delimit symbol names with
    // straight single quotes, so this pulls out the type and member names without parsing the whole message.
    private static IReadOnlyList<string> QuotedTokens(string message)
    {
        var tokens = new List<string>();
        foreach (Match m in QuotedTokenRegex().Matches(message))
            tokens.Add(m.Groups[1].Value);
        return tokens;
    }

    // Standard iterative Levenshtein edit distance, used only to rank a handful of candidate names, so the O(nm)
    // cost is on short identifiers and never on large inputs.
    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedTokenRegex();
}

/// <summary>
///     A repair packet (R6): the fix context attached to a speculative-typecheck diagnostic, so an agent can
///     correct an API-shape mistake without a round-trip to go read the type.
/// </summary>
/// <param name="DiagnosticId">The diagnostic id the packet explains (for example <c>CS1061</c>).</param>
/// <param name="Explanation">A one-line, plain explanation of the error and the suggested direction.</param>
/// <param name="Candidates">The nearest-name suggestions, closest first; empty when none could be found.</param>
/// <param name="Members">The receiver type's member signatures, when the diagnostic is a missing member; otherwise empty.</param>
public sealed record RepairPacket(
    string DiagnosticId,
    string Explanation,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<SymbolSignature> Members);
