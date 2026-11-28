using System.Diagnostics.CodeAnalysis;

namespace Fuse.Identity;

/// <summary>
///     The content address of one test slice: a hash of everything a test project's result depends on, under which the
///     slice test cache keeps that result. Equal keys mean the same tests ran against the same inputs.
/// </summary>
internal sealed record SliceKey
{
    private SliceKey(string value) => Value = value;

    /// <summary>The key as the cache stores it: hexadecimal characters.</summary>
    public string Value { get; }

    /// <summary>Reads <paramref name="text"/> as a key; false when it is blank or holds a character other than a hexadecimal digit.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SliceKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text) || !text.All(char.IsAsciiHexDigit))
            return false;
        key = new SliceKey(text);
        return true;
    }

    public override string ToString() => Value;
}
