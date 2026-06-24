using System.Text;

namespace Fuse.Fusion.Session;

/// <summary>
///     Generates a unified diff between two versions of a file's text, used to re-send only what changed across
///     turns of a session instead of the whole file.
/// </summary>
/// <remarks>
///     Line-based, using a longest-common-subsequence edit script grouped into hunks with a few lines of
///     context, in the standard <c>@@ -l,s +l,s @@</c> format. The diff is advisory context for an agent, not a
///     patch to apply mechanically, so it favours readability. Diffing is quadratic in line count, so files
///     above <see cref="MaxLines" /> return <see langword="null" /> and the caller falls back to the whole file.
/// </remarks>
public static class UnifiedDiffGenerator
{
    // Above this line count on either side, the quadratic LCS is skipped and the caller sends the whole file.
    private const int MaxLines = 2000;

    // Lines of unchanged context shown around each change.
    private const int ContextLines = 3;

    /// <summary>
    ///     Builds a unified diff from <paramref name="before" /> to <paramref name="after" />.
    /// </summary>
    /// <param name="before">The previously emitted content.</param>
    /// <param name="after">The current content.</param>
    /// <returns>
    ///     The unified diff text, or <see langword="null" /> when the inputs are identical, either side exceeds
    ///     the line cap, or the change covers most of the file (so a diff would not be smaller than the whole).
    /// </returns>
    public static string? Build(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return null;

        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);
        if (beforeLines.Length > MaxLines || afterLines.Length > MaxLines)
            return null;

        var ops = Diff(beforeLines, afterLines);

        // If almost everything changed, a diff is not smaller than the file; signal a whole-file resend.
        var changed = ops.Count(o => o.Kind != OpKind.Equal);
        var total = Math.Max(1, beforeLines.Length + afterLines.Length);
        if (changed > total * 0.6)
            return null;

        return Render(ops, beforeLines, afterLines);
    }

    private enum OpKind { Equal, Delete, Insert }

    private readonly record struct Op(OpKind Kind, int BeforeIndex, int AfterIndex);

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // Longest-common-subsequence edit script over lines.
    private static List<Op> Diff(string[] before, string[] after)
    {
        var n = before.Length;
        var m = after.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<Op>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (string.Equals(before[a], after[b], StringComparison.Ordinal))
            {
                ops.Add(new Op(OpKind.Equal, a, b));
                a++; b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                ops.Add(new Op(OpKind.Delete, a, b));
                a++;
            }
            else
            {
                ops.Add(new Op(OpKind.Insert, a, b));
                b++;
            }
        }

        while (a < n) ops.Add(new Op(OpKind.Delete, a++, b));
        while (b < m) ops.Add(new Op(OpKind.Insert, a, b++));
        return ops;
    }

    // Groups the edit script into hunks (changes plus surrounding context) and renders unified-diff text.
    private static string Render(List<Op> ops, string[] before, string[] after)
    {
        // Mark which op indices are within context distance of a change, so runs of pure context far from any
        // change are dropped between hunks.
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == OpKind.Equal)
                continue;
            for (var c = Math.Max(0, i - ContextLines); c <= Math.Min(ops.Count - 1, i + ContextLines); c++)
                keep[c] = true;
        }

        var builder = new StringBuilder();
        var index = 0;
        while (index < ops.Count)
        {
            if (!keep[index]) { index++; continue; }

            var hunkStart = index;
            while (index < ops.Count && keep[index]) index++;
            var hunkEnd = index; // exclusive

            // Hunk header line ranges (1-based). Count lines present on each side within the hunk.
            var beforeStart = ops[hunkStart].BeforeIndex;
            var afterStart = ops[hunkStart].AfterIndex;
            var beforeCount = 0;
            var afterCount = 0;
            for (var k = hunkStart; k < hunkEnd; k++)
            {
                if (ops[k].Kind != OpKind.Insert) beforeCount++;
                if (ops[k].Kind != OpKind.Delete) afterCount++;
            }

            builder.Append("@@ -").Append(beforeStart + 1).Append(',').Append(beforeCount)
                .Append(" +").Append(afterStart + 1).Append(',').Append(afterCount).Append(" @@\n");

            for (var k = hunkStart; k < hunkEnd; k++)
            {
                var op = ops[k];
                switch (op.Kind)
                {
                    case OpKind.Equal:
                        builder.Append(' ').Append(before[op.BeforeIndex]).Append('\n');
                        break;
                    case OpKind.Delete:
                        builder.Append('-').Append(before[op.BeforeIndex]).Append('\n');
                        break;
                    case OpKind.Insert:
                        builder.Append('+').Append(after[op.AfterIndex]).Append('\n');
                        break;
                }
            }
        }

        return builder.ToString();
    }
}
