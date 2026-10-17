using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Check;

/// <summary>
///     Finds the file a build generates that declares what an error is about, such as the API client a build writes into
///     <c>obj</c> from another project's output. Fuse reads such a file as the last build left it, so an error about a
///     member it lacks can mean only that the build has not written it again since its source changed.
/// </summary>
/// <remarks>
///     A file counts as generated when it lies in a <c>bin</c> or <c>obj</c> folder of the project that compiles it
///     (<see cref="PathRules.IsBuildOutput"/>). The error is about the symbols the compiler bound, or tried to bind, at its
///     position, and the type of the expression whose member is accessed there; a missing member's error names no symbol
///     but has the type that lacks it.
/// </remarks>
internal sealed class GeneratedSource
{
    private readonly RepoRoot _root;

    /// <summary>Whether each declaring file seen so far is generated, by absolute path.</summary>
    private readonly Dictionary<string, bool> _generated = new(StringComparer.Ordinal);

    public GeneratedSource(RepoRoot root) => _root = root;

    /// <summary>Each error, with the generated file that declares what it is about when there is one, in the same order.</summary>
    public async Task<IReadOnlyList<IntroducedError>> AttachAsync(Solution solution, IReadOnlyList<IntroducedError> errors, CancellationToken cancellationToken)
    {
        var result = new List<IntroducedError>(errors.Count);
        foreach (var error in errors)
        {
            var generated = await DeclaringFileAsync(solution, error.Error, cancellationToken).ConfigureAwait(false);
            result.Add(generated is null ? error : error with { GeneratedFile = generated });
        }

        return result;
    }

    /// <summary>The repository-relative path of the generated file that declares what <paramref name="error"/> is about, or null.</summary>
    private async Task<string?> DeclaringFileAsync(Solution solution, CompilerError error, CancellationToken cancellationToken)
    {
        var ids = solution.GetDocumentIdsWithFilePath(_root.PathOf(error.Path).Absolute);
        if (ids.IsEmpty || solution.GetDocument(ids[0]) is not { } document)
            return null;
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
            return null;
        var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (error.Line < 1 || error.Line > text.Lines.Count)
            return null;
        var line = text.Lines[error.Line - 1];
        var position = Math.Min(line.Start + Math.Max(0, error.Column - 1), Math.Max(line.Start, line.End - 1));
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // The token at the error and the few expressions around it: a name, its member access, the call that holds it.
        var node = root.FindToken(position).Parent;
        for (var depth = 0; node is not null && depth < 4; depth++, node = node.Parent)
        {
            foreach (var symbol in SymbolsAt(model, node, cancellationToken))
            {
                if (GeneratedFileOf(symbol) is { } file)
                    return file;
            }

            if (node is StatementSyntax or MemberDeclarationSyntax)
                break;
        }

        return null;
    }

    private static IEnumerable<ISymbol> SymbolsAt(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(node, cancellationToken);
        if (info.Symbol is { } symbol)
            yield return symbol;
        foreach (var candidate in info.CandidateSymbols)
            yield return candidate;
        if (model.GetTypeInfo(node, cancellationToken).Type is { } type)
            yield return type;
        if (node is MemberAccessExpressionSyntax access && model.GetTypeInfo(access.Expression, cancellationToken).Type is { } receiver)
            yield return receiver;
    }

    /// <summary>The generated file that declares <paramref name="symbol"/>, or the type that holds it, or null.</summary>
    private string? GeneratedFileOf(ISymbol symbol)
    {
        var declared = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        foreach (var current in new[] { symbol, declared })
        {
            if (current is null)
                continue;
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree.FilePath;
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                    continue;
                if (IsGenerated(path))
                    return _root.PathOf(path).Relative;
            }
        }

        return null;
    }

    /// <summary>
    ///     True for a file in a <c>bin</c> or <c>obj</c> folder below the folder of the nearest project file above it. Each
    ///     file is decided once per engine: the errors of one check name the same few declaring files many times.
    /// </summary>
    private bool IsGenerated(string path)
    {
        lock (_generated)
        {
            if (_generated.TryGetValue(path, out var known))
                return known;
        }

        var generated = false;
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj").Any())
            {
                generated = PathRules.IsBuildOutput(directory, path);
                break;
            }
        }

        lock (_generated)
            _generated[path] = generated;
        return generated;
    }
}
