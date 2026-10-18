using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>
///     The terminal escape sequences a child process can print around its words. Some test runners color their output
///     on Linux and macOS even when it is redirected, so a parser reads the output with the sequences removed.
/// </summary>
internal static partial class AnsiSequences
{
    /// <summary><paramref name="output"/> with every escape sequence removed.</summary>
    public static string Strip(string output) => Sequence().Replace(output, "");

    [GeneratedRegex("\u001b\\[[0-?]*[ -/]*[@-~]|\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)|\u001b[@-Z\\\\-_]")]
    private static partial Regex Sequence();
}
