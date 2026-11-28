using System.Diagnostics.CodeAnalysis;

namespace Fuse.Identity;

/// <summary>
///     The identity of a session that writes files: either a harness session, whose id the agent harness gives, or a
///     session a launcher registered by name with <c>fuse session begin</c>. The engine stores a registered session
///     under <see cref="RegisteredPrefix"/> followed by its name (<see cref="StorageKey"/>), and every surface names it
///     by its name alone (<see cref="DisplayName"/>), so this type is the one place that knows both spellings.
/// </summary>
internal sealed record SessionId
{
    /// <summary>What a registered session's stored id starts with, followed by the name the launcher gave.</summary>
    public const string RegisteredPrefix = "registered:";

    /// <summary>The longest session id the engine records. A harness's ids are far shorter; a longer one is refused.</summary>
    public const int MaxLength = 256;

    /// <summary>How long a registration lasts without being begun again: long enough that an idle engine dying does not end it.</summary>
    public static readonly TimeSpan RegistrationLifetime = TimeSpan.FromHours(4);

    /// <summary>The longest name a registration accepts.</summary>
    public const int MaxRegistrationNameLength = 64;

    private SessionId(string storageKey, bool isRegistered)
    {
        StorageKey = storageKey;
        IsRegistered = isRegistered;
    }

    /// <summary>The id as the engine stores it: a harness id as given, a registration as the prefix followed by its name.</summary>
    public string StorageKey { get; }

    /// <summary>True for a session a launcher registered by name, false for a harness session.</summary>
    public bool IsRegistered { get; }

    /// <summary>The id as every surface names it: a registration's name without the prefix, a harness id as given.</summary>
    public string DisplayName => IsRegistered ? StorageKey[RegisteredPrefix.Length..] : StorageKey;

    /// <summary>
    ///     The stem of the session's file name: <see cref="DisplayName"/> with every character a file name cannot hold
    ///     (<c>:</c>, <c>/</c> and the rest) replaced by <c>-</c> and leading and trailing dots removed, or
    ///     <c>session</c> when nothing is left.
    /// </summary>
    public string FileKey
    {
        get
        {
            var file = new string([.. DisplayName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]).Trim('.');
            return file.Length == 0 ? "session" : file;
        }
    }

    /// <summary>True when <paramref name="id"/> can be recorded: not blank, at most <see cref="MaxLength"/> characters, no control characters.</summary>
    public static bool IsValid([NotNullWhen(true)] string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxLength && !id.Any(char.IsControl);

    /// <summary>
    ///     True when <paramref name="name"/> can be registered: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or <c>.</c>. No
    ///     slash or colon, so a registration never aliases a harness session or acquires subagent semantics.
    /// </summary>
    public static bool IsValidRegistrationName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxRegistrationNameLength
        && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    ///     Reads a stored id: one that starts with <see cref="RegisteredPrefix"/> is a registration, any other valid id a
    ///     harness session. False when <paramref name="stored"/> is not a valid id.
    /// </summary>
    public static bool TryParse(string? stored, [NotNullWhen(true)] out SessionId? id)
    {
        id = null;
        if (!IsValid(stored))
            return false;
        id = new SessionId(stored, stored.StartsWith(RegisteredPrefix, StringComparison.Ordinal));
        return true;
    }

    /// <summary>The registration named <paramref name="name"/>, or false when the name is not one a registration accepts.</summary>
    public static bool TryParseRegistrationName(string? name, [NotNullWhen(true)] out SessionId? id)
    {
        id = null;
        if (!IsValidRegistrationName(name))
            return false;
        id = new SessionId(RegisteredPrefix + name, isRegistered: true);
        return true;
    }

    /// <summary>The name every surface prints for the stored id <paramref name="stored"/>: the registration's name, or the id as given.</summary>
    public static string DisplayNameOf(string stored) =>
        stored.StartsWith(RegisteredPrefix, StringComparison.Ordinal) ? stored[RegisteredPrefix.Length..] : stored;

    public override string ToString() => StorageKey;
}
