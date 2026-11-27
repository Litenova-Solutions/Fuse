using System.Diagnostics.CodeAnalysis;

namespace Fuse.Identity;

/// <summary>
///     A tree stamp: 12 lowercase hexadecimal characters that name the state of the working tree a check or a test plan
///     answered for, so two answers can be compared without comparing their files.
/// </summary>
internal sealed record TreeStampValue
{
    /// <summary>How many characters a stamp has.</summary>
    public const int Length = 12;

    private TreeStampValue(string value) => Value = value;

    /// <summary>The stamp's characters.</summary>
    public string Value { get; }

    /// <summary>Reads <paramref name="text"/> as a stamp; false unless it is exactly 12 lowercase hexadecimal characters.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out TreeStampValue? stamp)
    {
        stamp = null;
        if (text is not { Length: Length } || !text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            return false;
        stamp = new TreeStampValue(text);
        return true;
    }

    public override string ToString() => Value;
}
