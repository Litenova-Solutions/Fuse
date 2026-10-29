namespace Fuse.Operations;

/// <summary>
///     What a mistyped Fuse option likely means: the known option within two edits of it. Only <c>fuse build</c> and
///     <c>fuse test</c> ask, because only they pass unknown options to dotnet, where a typo of a Fuse flag would
///     otherwise run with the default.
/// </summary>
internal static class NearMiss
{
    /// <summary>The known option <paramref name="token"/> is a near miss of, or null when it is none.</summary>
    /// <param name="known">The command's Fuse options, without their values.</param>
    public static string? Suggest(string token, IReadOnlyList<string> known)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
            return null;
        var option = token.Contains('=') ? token[..token.IndexOf('=')] : token;
        if (option.Length <= 2 || known.Contains(option))
            return null;
        string? best = null;
        var bestDistance = 3;
        foreach (var name in known)
        {
            var distance = Distance(option, name);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = name;
            }
        }

        return bestDistance <= 2 ? best : null;
    }

    /// <summary>How many insertions, deletions and substitutions turn <paramref name="a"/> into <paramref name="b"/>.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }

        return previous[b.Length];
    }
}
