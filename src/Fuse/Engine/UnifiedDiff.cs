using System.Globalization;
using System.Text;

namespace Fuse.Engine;

/// <summary>
///     Parses git-style unified diffs and applies them to file texts without touching disk. The judge reads each
///     candidate diff with <see cref="Parse"/> and forks the current solution with <see cref="ApplyToText"/>.
/// </summary>
internal static class UnifiedDiff
{
    /// <summary>One file's patch: the paths the diff names and the hunks that change them.</summary>
    /// <param name="OldPath">The path after ---, without its a/ prefix, or /dev/null when the file is new.</param>
    /// <param name="NewPath">The path after +++, without its b/ prefix, or /dev/null when the file is deleted.</param>
    /// <param name="Hunks">The hunks in file order.</param>
    public sealed record FilePatch(string OldPath, string NewPath, IReadOnlyList<Hunk> Hunks)
    {
        /// <summary>True when the patch creates the file.</summary>
        public bool IsNew => OldPath == "/dev/null";

        /// <summary>True when the patch deletes the file.</summary>
        public bool IsDeleted => NewPath == "/dev/null";

        /// <summary>The repository-relative path the patch touches.</summary>
        public string Path => IsNew ? NewPath : OldPath;
    }

    /// <summary>One hunk: the lines it replaces and the lines it replaces them with.</summary>
    /// <param name="OldStart">One-based first line in the old file, or 0 when the hunk adds at the start.</param>
    /// <param name="OldCount">How many old lines the hunk holds: context and removals.</param>
    /// <param name="NewStart">One-based first line in the new file, or 0 when the hunk deletes to empty.</param>
    /// <param name="NewCount">How many new lines the hunk holds: context and additions.</param>
    /// <param name="Lines">The hunk lines in order.</param>
    /// <param name="OldNoNewline">True when the old file ends without a newline inside this hunk.</param>
    /// <param name="NewNoNewline">True when the new file ends without a newline inside this hunk.</param>
    public sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<HunkLine> Lines, bool OldNoNewline, bool NewNoNewline);

    /// <summary>One hunk line.</summary>
    /// <param name="Kind">Context (a space), removal (-) or addition (+).</param>
    /// <param name="Text">The line without its kind marker.</param>
    public sealed record HunkLine(char Kind, string Text);

    /// <summary>Parses a unified diff holding one or more files, with or without diff --git headers.</summary>
    /// <returns>The file patches, or the first problem when the text is not a unified diff.</returns>
    public static (IReadOnlyList<FilePatch> Files, string? Error) Parse(string diff)
    {
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        for (var k = 0; k < lines.Length; k++)
        {
            if (lines[k].EndsWith('\r'))
                lines[k] = lines[k][..^1];
        }

        var files = new List<FilePatch>();
        string? oldPath = null;
        string? newPath = null;
        var hunks = new List<Hunk>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (oldPath is not null && newPath is null)
                    return ([], $"the diff names no new file after '--- {oldPath}'");
                if (oldPath is not null && newPath is not null)
                {
                    files.Add(new FilePatch(oldPath, newPath, hunks));
                    hunks = [];
                }

                oldPath = null;
                newPath = null;
                i++;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (oldPath is not null && newPath is null)
                    return ([], $"the diff names no new file after '--- {oldPath}'");
                if (oldPath is not null && newPath is not null)
                {
                    files.Add(new FilePatch(oldPath, newPath, hunks));
                    hunks = [];
                }

                oldPath = StripPrefix(CleanPath(line["--- ".Length..]), "a/");
                i++;
                if (i >= lines.Length || !lines[i].StartsWith("+++ ", StringComparison.Ordinal))
                    return ([], $"the diff names no new file after '--- {oldPath}'");
                newPath = StripPrefix(CleanPath(lines[i]["+++ ".Length..]), "b/");
                i++;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
                return ([], "the diff names a new file before it names an old one");

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (oldPath is null || newPath is null)
                    return ([], "the diff holds a hunk before it names a file");
                var hunk = ParseHunk(lines, ref i, out var hunkError);
                if (hunk is null)
                    return ([], hunkError);
                if (i < lines.Length && IsBodyLine(lines[i]) && !lines[i].StartsWith("--- ", StringComparison.Ordinal) && !lines[i].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    // A patch file that ends with a blank line leaves empty lines after the last hunk; they end
                    // the diff, not the hunk. Anything else past the header counts is still a hunk that is too long.
                    for (var j = i; j < lines.Length; j++)
                    {
                        if (lines[j].Length != 0)
                            return ([], $"the hunk at '{line}' holds more lines than its header says");
                    }

                    i = lines.Length;
                }

                hunks.Add(hunk);
                continue;
            }

            i++;
        }

        if (oldPath is not null && newPath is null)
            return ([], $"the diff names no new file after '--- {oldPath}'");
        if (oldPath is not null && newPath is not null)
            files.Add(new FilePatch(oldPath, newPath, hunks));
        return (files, null);
    }

    /// <summary>Applies one file patch to the file's text, keeping the text's own line endings.</summary>
    /// <returns>The new text, or the reason the hunks do not match it.</returns>
    public static (string? Text, string? Error) ApplyToText(string baseText, FilePatch patch)
    {
        var newline = baseText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(baseText, out var baseEndsWithNewline);
        var baseCount = lines.Count;
        var offset = 0;
        for (var h = 0; h < patch.Hunks.Count; h++)
        {
            var hunk = patch.Hunks[h];
            var oldLines = new List<string>();
            var newLines = new List<string>();
            foreach (var hunkLine in hunk.Lines)
            {
                if (hunkLine.Kind is ' ' or '-')
                    oldLines.Add(hunkLine.Text);
                if (hunkLine.Kind is ' ' or '+')
                    newLines.Add(hunkLine.Text);
            }

            var pos = (hunk.OldStart == 0 ? 0 : hunk.OldStart - 1) + offset;
            if (pos < 0 || pos + oldLines.Count > lines.Count)
                return (null, $"{patch.Path}: hunk {h + 1} reaches past the end of the file");
            for (var j = 0; j < oldLines.Count; j++)
            {
                if (lines[pos + j] != oldLines[j])
                    return (null, $"{patch.Path}: hunk {h + 1} does not match at line {pos + j + 1}: expected '{oldLines[j]}' but found '{lines[pos + j]}'");
            }

            if (h == patch.Hunks.Count - 1 && hunk.OldStart + hunk.OldCount - 1 == baseCount && baseCount > 0)
            {
                if (baseEndsWithNewline && hunk.OldNoNewline)
                    return (null, $"{patch.Path}: hunk {h + 1} does not match: the file ends with a newline but the patch says it ends without one");
                if (!baseEndsWithNewline && !hunk.OldNoNewline)
                    return (null, $"{patch.Path}: hunk {h + 1} does not match: the file ends without a newline but the patch says it ends with one");
            }

            lines.RemoveRange(pos, oldLines.Count);
            lines.InsertRange(pos, newLines);
            offset += newLines.Count - oldLines.Count;
        }

        if (lines.Count == 0)
            return ("", null);
        var endsWithNewline = baseEndsWithNewline;
        if (patch.Hunks.Count > 0)
        {
            var last = patch.Hunks[^1];
            if (last.NewStart + last.NewCount - 1 == lines.Count)
                endsWithNewline = !last.NewNoNewline;
        }

        return (string.Join(newline, lines) + (endsWithNewline ? newline : ""), null);
    }

    /// <summary>Reads the hunk at lines[i], leaving the index past its body.</summary>
    /// <returns>The hunk, or null with the reason the hunk is not one.</returns>
    private static Hunk? ParseHunk(string[] lines, ref int i, out string? error)
    {
        var header = lines[i];
        if (!TryParseHunkHeader(header, out var oldStart, out var oldCount, out var newStart, out var newCount))
        {
            error = $"the diff holds a hunk header that names no lines: '{header}'";
            return null;
        }

        var body = new List<HunkLine>();
        var oldNoNewline = false;
        var newNoNewline = false;
        var oldSeen = 0;
        var newSeen = 0;
        var lastKind = '\0';
        i++;
        while (oldSeen < oldCount || newSeen < newCount || (i < lines.Length && lines[i].StartsWith('\\')))
        {
            if (i >= lines.Length)
            {
                error = $"the hunk at '{header}' holds fewer lines than its header says";
                return null;
            }

            var bodyLine = lines[i];
            if (bodyLine.StartsWith('\\'))
            {
                if (lastKind == '\0')
                {
                    error = $"the hunk at '{header}' marks a missing newline with no line before it";
                    return null;
                }

                if (lastKind is '-' or ' ')
                    oldNoNewline = true;
                if (lastKind is '+' or ' ')
                    newNoNewline = true;
                i++;
                continue;
            }

            if (bodyLine.Length == 0)
            {
                body.Add(new HunkLine(' ', ""));
                oldSeen++;
                newSeen++;
                lastKind = ' ';
                i++;
                continue;
            }

            var kind = bodyLine[0];
            if (kind is not (' ' or '-' or '+'))
            {
                error = $"the hunk at '{header}' holds fewer lines than its header says";
                return null;
            }

            body.Add(new HunkLine(kind, bodyLine[1..]));
            if (kind is ' ' or '-')
                oldSeen++;
            if (kind is ' ' or '+')
                newSeen++;
            lastKind = kind;
            i++;
        }

        error = null;
        return new Hunk(oldStart, oldCount, newStart, newCount, body, oldNoNewline, newNoNewline);
    }

    /// <summary>Reads the line ranges of a hunk header; a count the header leaves out reads as one.</summary>
    private static bool TryParseHunkHeader(string header, out int oldStart, out int oldCount, out int newStart, out int newCount)
    {
        oldStart = 0;
        oldCount = 0;
        newStart = 0;
        newCount = 0;
        var end = header.IndexOf(" @@", StringComparison.Ordinal);
        if (end < 0)
            return false;
        var ranges = header["@@ ".Length..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ranges.Length == 2
            && TryParseRange(ranges[0], '-', out oldStart, out oldCount)
            && TryParseRange(ranges[1], '+', out newStart, out newCount);
    }

    /// <summary>Reads one hunk header range such as -12,4.</summary>
    private static bool TryParseRange(string range, char sign, out int start, out int count)
    {
        start = 0;
        count = 1;
        if (range.Length < 2 || range[0] != sign)
            return false;
        var parts = range[1..].Split(',');
        if (parts.Length > 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0)
            return false;
        return parts.Length == 1 || (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0);
    }

    /// <summary>True when the line reads as a hunk body line: context, removal, addition, empty or a newline marker.</summary>
    private static bool IsBodyLine(string line) =>
        line.Length == 0 || line[0] is ' ' or '-' or '+' || line.StartsWith('\\');

    /// <summary>Splits text into lines without terminators, naming whether it ends with one.</summary>
    private static List<string> SplitLines(string text, out bool endsWithNewline)
    {
        if (text.Length == 0)
        {
            endsWithNewline = false;
            return [];
        }

        var parts = text.Split('\n');
        endsWithNewline = parts[^1].Length == 0;
        var count = endsWithNewline ? parts.Length - 1 : parts.Length;
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
            lines.Add(parts[i].EndsWith('\r') ? parts[i][..^1] : parts[i]);
        return lines;
    }

    /// <summary>The path a --- or +++ line names: without its timestamp, unquoted.</summary>
    private static string CleanPath(string rest)
    {
        var tab = rest.IndexOf('\t');
        return Unquote((tab < 0 ? rest : rest[..tab]).TrimEnd());
    }

    /// <summary>The path without its a/ or b/ prefix, or the path itself when it has none.</summary>
    private static string StripPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    /// <summary>Unquotes a double-quoted path, decoding its escapes as UTF-8 bytes.</summary>
    private static string Unquote(string path)
    {
        if (path.Length < 2 || !path.StartsWith('"') || !path.EndsWith('"'))
            return path;
        var inner = path[1..^1];
        var text = new StringBuilder(inner.Length);
        var k = 0;
        while (k < inner.Length)
        {
            if (inner[k] != '\\' || k + 1 >= inner.Length)
            {
                text.Append(inner[k]);
                k++;
                continue;
            }

            var next = inner[k + 1];
            if (next is '\\' or '"')
            {
                text.Append(next);
                k += 2;
            }
            else if (next == 'n')
            {
                text.Append('\n');
                k += 2;
            }
            else if (next == 't')
            {
                text.Append('\t');
                k += 2;
            }
            else if (next is >= '0' and <= '7')
            {
                var bytes = new List<byte>();
                while (k + 3 < inner.Length && inner[k] == '\\' && inner[k + 1] is >= '0' and <= '7' && inner[k + 2] is >= '0' and <= '7' && inner[k + 3] is >= '0' and <= '7')
                {
                    bytes.Add((byte)((inner[k + 1] - '0') * 64 + (inner[k + 2] - '0') * 8 + (inner[k + 3] - '0')));
                    k += 4;
                }

                if (bytes.Count == 0)
                {
                    text.Append(next);
                    k += 2;
                }
                else
                {
                    text.Append(Encoding.UTF8.GetString([.. bytes]));
                }
            }
            else
            {
                text.Append(next);
                k += 2;
            }
        }

        return text.ToString();
    }
}
