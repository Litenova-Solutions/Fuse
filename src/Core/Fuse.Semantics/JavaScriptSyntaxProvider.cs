using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     A syntax-tier provider for JavaScript and TypeScript: a bounded, offline line lexer that extracts
///     top-level and nested <c>class</c> and <c>function</c> declarations, plus arrow-function and
///     function-expression assignments (<c>const x = (...) =&gt; ...</c>), as symbols and chunks (no language
///     server, no native grammar). It widens language breadth through the same seam as C# and Python; the
///     semantic typed graph for these languages is out of scope (syntax tier only).
/// </summary>
/// <remarks>
///     This is the offline-regex tier toward broader coverage. A tree-sitter-backed extractor can later replace
///     this class behind the same <see cref="ILanguageSyntaxProvider" /> seam without changing the indexer or the
///     retrieval features, which all operate over the language-agnostic symbol, chunk, and edge tables.
/// </remarks>
public sealed partial class JavaScriptSyntaxProvider : ILanguageSyntaxProvider
{
    /// <inheritdoc />
    public string Language => "javascript";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } =
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"];

    /// <inheritdoc />
    public SyntaxExtractionResult Extract(string normalizedPath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return SyntaxExtractionResult.Empty;

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var match = Declaration().Match(line);
            if (!match.Success)
                continue;

            // The keyword group ("class"/"function") names a declaration; otherwise it is a const/let/var bound
            // to an arrow function or function expression, which we treat as a function for retrieval.
            var keyword = match.Groups["kw"].Value;
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["name2"].Value;
            if (string.IsNullOrEmpty(name))
                continue;

            var kind = keyword == "class" ? "class" : "function";
            var lineNumber = i + 1;
            var stableKey = $"{kind}:{name}:{lineNumber}";
            var symbolId = $"js:{normalizedPath}:{stableKey}";
            var signature = line.Trim();

            symbols.Add(new SymbolRecord(
                SymbolId: symbolId,
                FilePath: normalizedPath,
                Kind: kind,
                Name: name,
                FullyQualifiedName: name,
                Signature: signature,
                StartLine: lineNumber,
                EndLine: lineNumber,
                // An exported declaration is the module's public API; a leading-underscore name is private by convention.
                IsPublicApi: line.Contains("export", StringComparison.Ordinal) && !name.StartsWith('_')));

            chunks.Add(new ChunkRecord(
                ChunkId: $"chunk:{symbolId}",
                FilePath: normalizedPath,
                Kind: kind,
                StableKey: stableKey,
                StartLine: lineNumber,
                EndLine: lineNumber,
                TextHash: Hash(signature),
                TokenEstimate: (signature.Length + 3) / 4,
                ReducedTokenEstimate: (signature.Length + 3) / 4,
                SymbolId: symbolId,
                Name: name,
                Signature: signature,
                Body: signature,
                SymbolsText: name));
        }

        return new SyntaxExtractionResult(symbols, chunks);
    }

    private static string Hash(string text) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text)).ToString("x16");

    // A class or function declaration (optionally exported/default/async), or a const/let/var bound to an arrow
    // function or function expression. The two name groups keep the declaration and assignment forms distinct.
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:(?<kw>class|function)\*?\s+(?<name>[A-Za-z_$][\w$]*)|(?:const|let|var)\s+(?<name2>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[A-Za-z_$][\w$]*\s*=>))")]
    private static partial Regex Declaration();
}
