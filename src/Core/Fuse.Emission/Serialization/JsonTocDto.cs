namespace Fuse.Emission.Serialization;

/// <summary>
///     JSON shape for the table-of-contents document: a file count, an estimated total cost to read every
///     listed file, and one entry per file.
/// </summary>
public sealed class JsonTocDto
{
    /// <summary>The number of files in the table of contents.</summary>
    public int Files { get; set; }

    /// <summary>The summed per-file token cost of reading every listed file under the current reduction.</summary>
    public long ReadCostTokens { get; set; }

    /// <summary>The per-file entries, sorted by path.</summary>
    public JsonTocFileDto[] Entries { get; set; } = [];
}

/// <summary>
///     JSON shape for one file in the table of contents.
/// </summary>
public sealed class JsonTocFileDto
{
    /// <summary>The normalized, forward-slash relative path of the file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The token cost of reading this file under the current reduction settings.</summary>
    public long Tokens { get; set; }

    /// <summary>The declared types and their members, or an empty array when no outline is available.</summary>
    public JsonTocSymbolDto[] Symbols { get; set; } = [];
}

/// <summary>
///     JSON shape for one declared type and its members in the table of contents.
/// </summary>
public sealed class JsonTocSymbolDto
{
    /// <summary>The declaration kind, such as <c>class</c> or <c>interface</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The simple name of the type.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The declared member names, or an empty array when none were resolved.</summary>
    public string[] Members { get; set; } = [];
}
