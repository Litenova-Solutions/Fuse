// bodyintegrity: the regression guard for the string-literal-preservation fixes
// (raw-string masking and the tightened connection-string redactor) and for the
// symbol-level emission changes. It parses every raw .cs file with Roslyn (an
// independent oracle, not Fuse's own scanner) to collect the verbatim text of each
// non-trivial string literal, then checks how many of those literals survive
// byte-identical in the fused output. Optionally it also reports whether the fused
// output still parses as C# without errors.
//
// Body-integrity is meaningful only when bodies are kept (levels none / standard /
// aggressive). Skeleton and publicApi drop bodies by design, so the caller should
// not gate on the literal ratio for those arms; pass --parse-check off there too.
//
// Usage:
//   bodyintegrity <sourceDir> <fusedFile> [--parse-check] [--min-length N]
//
// Output: a JSON object on stdout:
//   { literalsTotal, literalsIntact, intactRatio, parseChecked, parses, parseErrorSample }
//
// Notes:
// - Redaction can legitimately replace a secret literal with [REDACTED:...]; run the
//   guard against output produced with redaction disabled, or accept that redacted
//   literals count as not-intact. The harness runs it on the reduction-only arms.
// - A literal shorter than --min-length (default 8) is ignored: short literals like
//   "" or "id" collide with unrelated text and would inflate the ratio.

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: bodyintegrity <sourceDir> <fusedFile> [--parse-check] [--min-length N]");
    return 2;
}

var sourceDir = args[0];
var fusedFile = args[1];
var parseCheck = args.Contains("--parse-check");
var minLength = 8;
var minIdx = Array.IndexOf(args, "--min-length");
if (minIdx >= 0 && minIdx + 1 < args.Length && int.TryParse(args[minIdx + 1], out var parsed))
    minLength = parsed;

if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"source dir not found: {sourceDir}");
    return 2;
}

if (!File.Exists(fusedFile))
{
    Console.Error.WriteLine($"fused file not found: {fusedFile}");
    return 2;
}

var csFiles = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.EndsWith(".g.cs", StringComparison.Ordinal)
                && !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
    .ToList();

// Collect the verbatim inner text of each non-trivial string literal from raw source.
var literals = new HashSet<string>(StringComparer.Ordinal);
foreach (var file in csFiles)
{
    var text = File.ReadAllText(file);
    SyntaxNode root;
    try
    {
        root = CSharpSyntaxTree.ParseText(text).GetRoot();
    }
    catch
    {
        continue;
    }

    foreach (var token in root.DescendantTokens())
    {
        var kind = token.Kind();
        var isString = kind is SyntaxKind.StringLiteralToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken;
        if (!isString)
            continue;

        // token.Text is the verbatim source slice of the literal (delimiters and escape sequences intact).
        // Reduction preserves a literal byte-for-byte, so the raw text must appear in the fused output. We
        // compare the raw text, not the decoded ValueText, because the output keeps the escaped source form
        // (for example \n stays as the two characters backslash-n, not a real newline).
        var rawLiteral = token.Text;
        if (rawLiteral.Length >= minLength)
            literals.Add(rawLiteral);
    }
}

var fusedRaw = File.ReadAllText(fusedFile);
var intact = literals.Where(l => fusedRaw.Contains(l, StringComparison.Ordinal)).Count();

bool? parses = null;
string[] parseErrors = [];
if (parseCheck)
{
    var diagnostics = CSharpSyntaxTree.ParseText(fusedRaw).GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToList();
    parses = diagnostics.Count == 0;
    parseErrors = diagnostics.Take(10).Select(d => d.ToString()).ToArray();
}

var result = new
{
    sourceDir,
    fusedFile,
    literalsTotal = literals.Count,
    literalsIntact = intact,
    intactRatio = literals.Count == 0 ? 1.0 : Math.Round((double)intact / literals.Count, 4),
    parseChecked = parseCheck,
    parses,
    parseErrorSample = parseErrors,
};

Console.Out.Write(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return 0;
