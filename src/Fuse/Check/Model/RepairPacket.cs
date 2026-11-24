namespace Fuse.Check.Model;

/// <summary>
///     Deterministic compiler facts for fixing one diagnostic without opening other files: a 5-line source window,
///     nearest accessible members or candidate overloads, missing <c>using</c> directives, or an unimplemented
///     interface member stub.
/// </summary>
/// <param name="SourceWindow">Up to 5 numbered source lines around the diagnostic line (2 before, error line, 2 after).</param>
/// <param name="Candidates">Nearest accessible members or candidate overload signatures, up to 5.</param>
/// <param name="SuggestedUsings">Exact <c>using Namespace;</c> directives declaring a missing type, up to 5.</param>
/// <param name="InterfaceStub">Ready-to-paste signature stub for an unimplemented interface member, or null.</param>
internal sealed record RepairPacket(
    IReadOnlyList<string> SourceWindow,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> SuggestedUsings,
    string? InterfaceStub = null)
{
    /// <summary>Up to 5 numbered source lines around the diagnostic line.</summary>
    public IReadOnlyList<string> SourceWindow { get; init; } = SourceWindow ?? [];

    /// <summary>Nearest accessible members or candidate overload signatures, up to 5.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Candidates ?? [];

    /// <summary>Exact <c>using Namespace;</c> directives declaring a missing type, up to 5.</summary>
    public IReadOnlyList<string> SuggestedUsings { get; init; } = SuggestedUsings ?? [];

    public bool Equals(RepairPacket? other) =>
        other is not null
        && EqualityContract == other.EqualityContract
        && SourceWindow.SequenceEqual(other.SourceWindow, StringComparer.Ordinal)
        && Candidates.SequenceEqual(other.Candidates, StringComparer.Ordinal)
        && SuggestedUsings.SequenceEqual(other.SuggestedUsings, StringComparer.Ordinal)
        && string.Equals(InterfaceStub, other.InterfaceStub, StringComparison.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var line in SourceWindow)
            hash.Add(line, StringComparer.Ordinal);
        foreach (var candidate in Candidates)
            hash.Add(candidate, StringComparer.Ordinal);
        foreach (var directive in SuggestedUsings)
            hash.Add(directive, StringComparer.Ordinal);
        hash.Add(InterfaceStub, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
