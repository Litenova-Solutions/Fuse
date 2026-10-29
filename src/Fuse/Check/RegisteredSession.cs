namespace Fuse.Check;

/// <summary>
///     An external writer a launcher registered as a session: files under <see cref="Patterns"/> count as
///     <see cref="Session"/>'s until <see cref="Expires"/>, when they count as no session's again.
/// </summary>
/// <param name="Session">The stored id, <c>registered:</c> followed by the name the launcher gave.</param>
/// <param name="Patterns">Repository-relative path patterns, in normalized form, as <see cref="Paths.PathPattern"/> matches them.</param>
/// <param name="Expires">When the registration ends on its own, in UTC.</param>
internal sealed record RegisteredSession(string Session, IReadOnlyList<string> Patterns, DateTimeOffset Expires);
