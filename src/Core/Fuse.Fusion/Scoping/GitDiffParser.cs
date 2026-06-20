using System.Text;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Parses unified <c>git diff</c> output into per-file <see cref="FileDiff" /> records.
/// </summary>
/// <remarks>
///     Split into a separate type from <see cref="GitChangeDetector" /> so the parsing can be tested without
///     spawning git. The parser is tolerant of additions, deletions, and renames: the target path is taken
///     from the <c>+++ b/</c> line, falling back to the <c>--- a/</c> line for deletions.
/// </remarks>
public static class GitDiffParser
{
    /// <summary>
    ///     Parses the output of <c>git diff --unified=N</c> into one <see cref="FileDiff" /> per changed file.
    /// </summary>
    /// <param name="diff">The raw unified diff text. A null or empty value yields an empty result.</param>
    /// <returns>One <see cref="FileDiff" /> per file section, in the order they appear.</returns>
    public static IReadOnlyList<FileDiff> Parse(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
            return [];

        var results = new List<FileDiff>();
        var lines = diff.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Collect this file's section: every line up to the next "diff --git" header.
            var sectionStart = i + 1;
            var sectionEnd = sectionStart;
            while (sectionEnd < lines.Length && !lines[sectionEnd].StartsWith("diff --git ", StringComparison.Ordinal))
                sectionEnd++;

            var section = lines[sectionStart..sectionEnd];
            var parsed = ParseSection(lines[i], section);
            if (parsed is not null)
                results.Add(parsed);

            i = sectionEnd;
        }

        return results;
    }

    private static FileDiff? ParseSection(string header, string[] section)
    {
        string? newPath = null;
        string? oldPath = null;
        var hunkStart = -1;

        for (var i = 0; i < section.Length; i++)
        {
            var line = section[i];
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
                newPath = StripPathPrefix(line[4..]);
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
                oldPath = StripPathPrefix(line[4..]);
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                hunkStart = i;
                break;
            }
        }

        var path = newPath ?? oldPath ?? PathFromHeader(header);
        if (path is null)
            return null;

        var added = 0;
        var removed = 0;
        var hunks = new StringBuilder();
        if (hunkStart >= 0)
        {
            for (var i = hunkStart; i < section.Length; i++)
            {
                var line = section[i];
                hunks.Append(line).Append('\n');

                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('+'))
                    added++;
                else if (line.StartsWith('-'))
                    removed++;
            }
        }

        return new FileDiff(path, added, removed, hunks.ToString().TrimEnd('\n'));
    }

    private static string? StripPathPrefix(string value)
    {
        var path = value.Trim();
        if (path == "/dev/null")
            return null;
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path.Replace('\\', '/');
    }

    private static string? PathFromHeader(string header)
    {
        // "diff --git a/path b/path" -> take the b/ path.
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 ? StripPathPrefix(parts[^1]) : null;
    }
}
