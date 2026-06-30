using System.Text;

namespace Fuse.Indexing;

/// <summary>
///     The Porter stemming algorithm (Porter, 1980): reduces an English word to its stem so morphological
///     variants collapse to one term. <c>rounding</c>, <c>rounds</c>, and <c>rounded</c> all stem to
///     <c>round</c>; <c>calculate</c> and <c>calculation</c> both stem to <c>calcul</c>. Used to build the
///     stemmed full-text bridge field and to stem query terms the same way, so a query word matches a prose or
///     comment word that differs only by inflection, with no model.
/// </summary>
/// <remarks>
///     This is the classic five-step algorithm operating on lowercase ASCII letters. It is deterministic and
///     offline. Words shorter than three letters and tokens that are not all letters (identifiers, numbers) are
///     returned unchanged, so it stems prose without mangling code tokens or digits.
/// </remarks>
public static class PorterStemmer
{
    /// <summary>
    ///     Stems a single word. Non-alphabetic tokens and words of two letters or fewer are returned lowercased
    ///     but otherwise unchanged.
    /// </summary>
    /// <param name="word">The word to stem.</param>
    /// <returns>The stem, lowercased.</returns>
    public static string Stem(string word)
    {
        if (string.IsNullOrEmpty(word))
            return string.Empty;

        var lower = word.ToLowerInvariant();
        if (lower.Length <= 2 || !IsAllLetters(lower))
            return lower;

        var b = new StringBuilder(lower);
        Step1A(b);
        Step1B(b);
        Step1C(b);
        Step2(b);
        Step3(b);
        Step4(b);
        Step5A(b);
        Step5B(b);
        return b.ToString();
    }

    /// <summary>
    ///     Expands one or more text fields into a single space-joined string of distinct stemmed tokens, for
    ///     storage in the stemmed full-text bridge field. Each field is split into subword tokens (the same split
    ///     used for the subtokens field) and each token is stemmed, so inflected variants collapse to one stem.
    /// </summary>
    /// <param name="fields">The text fields to expand (for example a chunk's subtokens source and its comments).</param>
    /// <returns>The distinct stems joined by a space; empty when none qualify.</returns>
    public static string Expand(params string?[] fields)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var token in IdentifierSplitter.Split(field))
            {
                var stem = Stem(token);
                if (stem.Length > 0 && seen.Add(stem))
                    parts.Add(stem);
            }
        }

        return string.Join(' ', parts);
    }

    private static bool IsAllLetters(string s)
    {
        foreach (var ch in s)
        {
            if (ch is < 'a' or > 'z')
                return false;
        }

        return true;
    }

    private static bool IsConsonant(StringBuilder b, int i)
    {
        var ch = b[i];
        if (ch is 'a' or 'e' or 'i' or 'o' or 'u')
            return false;
        // 'y' is a consonant unless preceded by a consonant (so "toy" has y as vowel, "syzygy" has y as vowel).
        if (ch == 'y')
            return i == 0 || !IsConsonant(b, i - 1);
        return true;
    }

    // The measure m: the count of vowel-consonant sequences, the core of Porter's condition tests.
    private static int Measure(StringBuilder b, int end)
    {
        var m = 0;
        var i = 0;
        while (i < end && IsConsonant(b, i)) i++;
        while (i < end)
        {
            while (i < end && !IsConsonant(b, i)) i++;
            if (i >= end) break;
            m++;
            while (i < end && IsConsonant(b, i)) i++;
        }

        return m;
    }

    private static bool ContainsVowel(StringBuilder b, int end)
    {
        for (var i = 0; i < end; i++)
        {
            if (!IsConsonant(b, i))
                return true;
        }

        return false;
    }

    private static bool DoubleConsonant(StringBuilder b, int i) =>
        i >= 1 && b[i] == b[i - 1] && IsConsonant(b, i);

    // cvc: consonant-vowel-consonant where the final consonant is not w, x, or y. Used to decide *o in step 1b/5b.
    private static bool Cvc(StringBuilder b, int i)
    {
        if (i < 2 || !IsConsonant(b, i) || IsConsonant(b, i - 1) || !IsConsonant(b, i - 2))
            return false;
        var ch = b[i];
        return ch is not ('w' or 'x' or 'y');
    }

    private static bool EndsWith(StringBuilder b, string suffix)
    {
        if (suffix.Length > b.Length)
            return false;
        var offset = b.Length - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (b[offset + i] != suffix[i])
                return false;
        }

        return true;
    }

    private static void ReplaceEnd(StringBuilder b, string suffix, string replacement)
    {
        b.Length -= suffix.Length;
        b.Append(replacement);
    }

    private static void Step1A(StringBuilder b)
    {
        if (EndsWith(b, "sses")) ReplaceEnd(b, "sses", "ss");
        else if (EndsWith(b, "ies")) ReplaceEnd(b, "ies", "i");
        else if (EndsWith(b, "ss")) { }
        else if (EndsWith(b, "s")) b.Length -= 1;
    }

    private static void Step1B(StringBuilder b)
    {
        if (EndsWith(b, "eed"))
        {
            if (Measure(b, b.Length - 3) > 0)
                b.Length -= 1;
            return;
        }

        var applied = false;
        if (EndsWith(b, "ed") && ContainsVowel(b, b.Length - 2))
        {
            b.Length -= 2;
            applied = true;
        }
        else if (EndsWith(b, "ing") && ContainsVowel(b, b.Length - 3))
        {
            b.Length -= 3;
            applied = true;
        }

        if (!applied)
            return;

        if (EndsWith(b, "at")) b.Append('e');
        else if (EndsWith(b, "bl")) b.Append('e');
        else if (EndsWith(b, "iz")) b.Append('e');
        else if (DoubleConsonant(b, b.Length - 1) && !(EndsWith(b, "l") || EndsWith(b, "s") || EndsWith(b, "z")))
            b.Length -= 1;
        else if (Measure(b, b.Length) == 1 && Cvc(b, b.Length - 1))
            b.Append('e');
    }

    private static void Step1C(StringBuilder b)
    {
        if (EndsWith(b, "y") && ContainsVowel(b, b.Length - 1))
            b[b.Length - 1] = 'i';
    }

    private static void Step2(StringBuilder b)
    {
        foreach (var (suffix, replacement) in Step2Rules)
        {
            if (EndsWith(b, suffix))
            {
                if (Measure(b, b.Length - suffix.Length) > 0)
                    ReplaceEnd(b, suffix, replacement);
                return;
            }
        }
    }

    private static void Step3(StringBuilder b)
    {
        foreach (var (suffix, replacement) in Step3Rules)
        {
            if (EndsWith(b, suffix))
            {
                if (Measure(b, b.Length - suffix.Length) > 0)
                    ReplaceEnd(b, suffix, replacement);
                return;
            }
        }
    }

    private static void Step4(StringBuilder b)
    {
        foreach (var suffix in Step4Suffixes)
        {
            if (!EndsWith(b, suffix))
                continue;

            var stemEnd = b.Length - suffix.Length;
            if (Measure(b, stemEnd) <= 1)
                return;
            // "ion" only strips after s or t.
            if (suffix == "ion" && !(stemEnd > 0 && (b[stemEnd - 1] == 's' || b[stemEnd - 1] == 't')))
                return;
            b.Length = stemEnd;
            return;
        }
    }

    private static void Step5A(StringBuilder b)
    {
        if (!EndsWith(b, "e"))
            return;

        var stemEnd = b.Length - 1;
        var m = Measure(b, stemEnd);
        if (m > 1 || (m == 1 && !Cvc(b, stemEnd - 1)))
            b.Length -= 1;
    }

    private static void Step5B(StringBuilder b)
    {
        if (b.Length > 0 && EndsWith(b, "l") && DoubleConsonant(b, b.Length - 1) && Measure(b, b.Length) > 1)
            b.Length -= 1;
    }

    private static readonly (string Suffix, string Replacement)[] Step2Rules =
    [
        ("ational", "ate"), ("tional", "tion"), ("enci", "ence"), ("anci", "ance"), ("izer", "ize"),
        ("bli", "ble"), ("alli", "al"), ("entli", "ent"), ("eli", "e"), ("ousli", "ous"),
        ("ization", "ize"), ("ation", "ate"), ("ator", "ate"), ("alism", "al"), ("iveness", "ive"),
        ("fulness", "ful"), ("ousness", "ous"), ("aliti", "al"), ("iviti", "ive"), ("biliti", "ble"),
        ("logi", "log"),
    ];

    private static readonly (string Suffix, string Replacement)[] Step3Rules =
    [
        ("icate", "ic"), ("ative", ""), ("alize", "al"), ("iciti", "ic"), ("ical", "ic"),
        ("ful", ""), ("ness", ""),
    ];

    private static readonly string[] Step4Suffixes =
    [
        "al", "ance", "ence", "er", "ic", "able", "ible", "ant", "ement", "ment", "ent", "ion",
        "ou", "ism", "ate", "iti", "ous", "ive", "ize",
    ];
}
