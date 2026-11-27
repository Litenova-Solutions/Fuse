namespace Fuse.Protocol.Analysis;

/// <summary>A live session registration: an external writer a launcher registered as a session for its path patterns.</summary>
/// <param name="Name">The name the launcher gave the writer, without the stored <c>registered:</c> prefix.</param>
/// <param name="Paths">Repository-relative path patterns, in normalized form, as the launcher named them.</param>
/// <param name="Pid">The process the registration is tied to, or null for no process.</param>
/// <param name="ExpiresAtUtc">When the registration ends on its own, in UTC.</param>
internal sealed record ReportedSession(string Name, string[]? Paths = null, int? Pid = null, DateTimeOffset ExpiresAtUtc = default)
{
    /// <summary>The name. A line without it reads as empty, which no registration holds.</summary>
    public string Name { get; init; } = Name ?? "";

    /// <summary>The patterns. A line without the list reads as one that names none.</summary>
    public string[] Paths { get; init; } = Paths ?? [];
}
