using System.Diagnostics.CodeAnalysis;

namespace Fuse.Identity;

/// <summary>A git commit id: 40 (SHA-1) or 64 (SHA-256) lowercase hexadecimal characters.</summary>
internal sealed record CommitHash
{
    private CommitHash(string value) => Value = value;

    /// <summary>The id in lowercase hexadecimal.</summary>
    public string Value { get; }

    /// <summary>Reads <paramref name="text"/> as a commit id, ignoring surrounding whitespace and letter case; false when it is not one.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CommitHash? hash)
    {
        hash = null;
        var trimmed = text?.Trim();
        if (trimmed is not { Length: 40 or 64 } || !trimmed.All(char.IsAsciiHexDigit))
            return false;
        hash = new CommitHash(trimmed.ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}
