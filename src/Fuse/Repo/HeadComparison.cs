namespace Fuse.Repo;

/// <summary>
///     Decides whether a file in the working tree differs from the same file at HEAD. Line endings do not count: a
///     checkout with <c>core.autocrlf</c> writes CRLF where the commit has LF, and those files are not changes.
/// </summary>
internal static class HeadComparison
{
    /// <summary>True when the two versions differ, or when the file exists on one side only.</summary>
    /// <param name="atHead">The file's bytes at HEAD, or null when HEAD does not have it.</param>
    /// <param name="onDisk">The file's bytes in the working tree, or null when it is not on disk.</param>
    public static bool Differs(byte[]? atHead, byte[]? onDisk)
    {
        if (atHead is null || onDisk is null)
            return atHead is not null || onDisk is not null;
        return !SameIgnoringLineEndings(atHead, onDisk);
    }

    /// <summary>Compares the bytes with every carriage return skipped, wherever it is, not only one before a line feed.</summary>
    private static bool SameIgnoringLineEndings(byte[] a, byte[] b)
    {
        int i = 0, j = 0;
        while (true)
        {
            if (i < a.Length && a[i] == '\r')
            {
                i++;
                continue;
            }

            if (j < b.Length && b[j] == '\r')
            {
                j++;
                continue;
            }

            if (i == a.Length || j == b.Length)
                return i == a.Length && j == b.Length;
            if (a[i] != b[j])
                return false;
            i++;
            j++;
        }
    }
}
