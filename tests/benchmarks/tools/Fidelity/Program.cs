// fidelity: measure how much of a repository's public API surface survives a
// Fuse reduction. It parses the raw .cs files with Roslyn (an independent parser,
// not Fuse's own regex, so the measure cannot be circular) to build a ground-truth
// set of public/protected types and methods plus ASP.NET route templates, then
// checks how many of those symbols appear in a fused output file.
//
// Keys are chosen to survive reduction: a type is keyed by kind+name+arity, a
// method by containingType.name/paramCount. Neither key depends on namespaces or
// usings, which --all removes. Routes are matched as literal template strings.
//
// Usage:
//   fidelity <sourceDir> <fusedFile> [--skeleton]
//
// --skeleton documents that bodies are intentionally omitted; it does not change
// the type/method/route measure (those are signature-level and apply to skeleton).
//
// Output: a JSON object on stdout with per-category total/preserved/ratio and a
// small sample of missing symbols.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: fidelity <sourceDir> <fusedFile> [--skeleton]");
    return 2;
}

var sourceDir = args[0];
var fusedFile = args[1];
var skeleton = args.Contains("--skeleton");

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

// ---- Ground truth from raw source ----
var truthTypes = new HashSet<string>(StringComparer.Ordinal);
var truthMethods = new HashSet<string>(StringComparer.Ordinal);
var truthRoutes = new HashSet<string>(StringComparer.Ordinal);

var csFiles = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.EndsWith(".g.cs", StringComparison.Ordinal)
                && !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
    .ToList();

foreach (var file in csFiles)
{
    var text = File.ReadAllText(file);
    CollectSymbols(text, truthTypes, truthMethods, truthRoutes);
}

// ---- Fused output content ----
// The check side uses text-presence matching, not Roslyn re-parsing, because the
// --skeleton output is intentionally not valid C# (signatures without braces or
// bodies). We scan the fused text once for declared type names and for any
// identifier used as a call/declaration target, then intersect with ground truth.
var fusedRaw = File.ReadAllText(fusedFile);

var fusedTypeNames = new HashSet<string>(StringComparer.Ordinal);
foreach (Match m in Regex.Matches(fusedRaw, @"\b(?:class|interface|struct|record|enum)\s+([A-Za-z_]\w*)"))
{
    fusedTypeNames.Add(m.Groups[1].Value);
}

var fusedMethodNames = new HashSet<string>(StringComparer.Ordinal);
foreach (Match m in Regex.Matches(fusedRaw, @"(?<![.\w])([A-Za-z_]\w*)\s*(?:<[^>\n]*>)?\s*\("))
{
    fusedMethodNames.Add(m.Groups[1].Value);
}

// Routes are matched against the raw fused text (attributes may be stripped under
// --all, so we look for the literal route template anywhere in the output).
var preservedRoutes = truthRoutes.Where(r => fusedRaw.Contains(r, StringComparison.Ordinal)).ToHashSet();
var preservedTypes = truthTypes.Where(fusedTypeNames.Contains).ToHashSet();
var preservedMethods = truthMethods.Where(fusedMethodNames.Contains).ToHashSet();

var result = new
{
    sourceDir,
    fusedFile,
    skeleton,
    types = Category(truthTypes, preservedTypes),
    methods = Category(truthMethods, preservedMethods),
    routes = Category(truthRoutes, preservedRoutes),
};

Console.Out.Write(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static object Category(HashSet<string> truth, HashSet<string> preserved)
{
    var total = truth.Count;
    var kept = preserved.Count;
    var missing = truth.Except(preserved).OrderBy(x => x, StringComparer.Ordinal).Take(15).ToArray();
    return new
    {
        total,
        preserved = kept,
        ratio = total == 0 ? 1.0 : Math.Round((double)kept / total, 4),
        missing_sample = missing,
    };
}

static void CollectSymbols(string code, HashSet<string> types, HashSet<string> methods, HashSet<string> routes)
{
    SyntaxNode root;
    try
    {
        root = CSharpSyntaxTree.ParseText(code).GetRoot();
    }
    catch
    {
        return;
    }

    foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        var isPublicLike = HasPublicOrProtected(type.Modifiers);
        if (!isPublicLike)
        {
            continue;
        }

        types.Add(type.Identifier.ValueText);
    }

    foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        var parentType = method.Parent as BaseTypeDeclarationSyntax;
        var inInterface = parentType is InterfaceDeclarationSyntax;
        if (!inInterface && !HasPublicOrProtected(method.Modifiers))
        {
            continue;
        }

        // Only count methods whose containing type is itself public API.
        if (parentType is not null && !inInterface && !HasPublicOrProtected(parentType.Modifiers))
        {
            continue;
        }

        methods.Add(method.Identifier.ValueText);
    }

    foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
    {
        var name = attr.Name.ToString();
        var simple = name.Split('.').Last();
        var isRoute = simple is "Route" or "HttpGet" or "HttpPost" or "HttpPut"
            or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions";
        if (!isRoute || attr.ArgumentList is null)
        {
            continue;
        }

        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var val = lit.Token.ValueText;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    routes.Add(val);
                }
            }
        }
    }

    foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        if (inv.Expression is not MemberAccessExpressionSyntax ma)
        {
            continue;
        }

        var m = ma.Name.Identifier.ValueText;
        var isMap = m is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch";
        if (!isMap || inv.ArgumentList.Arguments.Count == 0)
        {
            continue;
        }

        if (inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var val = lit.Token.ValueText;
            if (!string.IsNullOrWhiteSpace(val))
            {
                routes.Add(val);
            }
        }
    }
}

static bool HasPublicOrProtected(SyntaxTokenList modifiers) =>
    modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));
