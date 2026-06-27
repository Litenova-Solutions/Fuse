using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     A second-language syntax-tier spike: a minimal, offline Python provider that extracts top-level and nested
///     <c>class</c> and <c>def</c> declarations as symbols and chunks using a bounded line lexer (no language
///     server). Its purpose is to prove the language-provider seam end to end (a non-C# file indexes,
///     full-text-searches, and localizes), not to be a full Python analyzer; the semantic graph for Python is
///     explicitly out of scope.
/// </summary>
public sealed partial class PythonSyntaxProvider : ILanguageSyntaxProvider
{
    /// <inheritdoc />
    public string Language => "python";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".py"];

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

            var kind = match.Groups["kw"].Value == "class" ? "class" : "function";
            var name = match.Groups["name"].Value;
            var lineNumber = i + 1;
            var stableKey = $"{kind}:{name}:{lineNumber}";
            var symbolId = $"py:{normalizedPath}:{stableKey}";
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
                IsPublicApi: !name.StartsWith('_')));

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

    // A top-level or indented class/def declaration: optional indentation, optional async, the keyword, and the name.
    [GeneratedRegex(@"^\s*(?:async\s+)?(?<kw>class|def)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Declaration();
}
