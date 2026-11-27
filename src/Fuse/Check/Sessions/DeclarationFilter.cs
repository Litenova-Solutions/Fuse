using System.Collections.Concurrent;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check.Model;
using Fuse.Paths;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Check.Sessions;

/// <summary>
///     Decides an error for a session without binding a session view, when everything the error can depend on is
///     bound at its site and traces to a declaration one session changed. The check first finds the errors in the
///     tree view; this filter runs in front of the view a session's check then reads, and the view decides every
///     error the filter declines, so a declined error is answered exactly as a check without the filter answers it.
/// </summary>
/// <remarks>
///     The filter declines whenever the answer could depend on what is not bound at the error site: more than one
///     bound or candidate symbol (overload resolution and ambiguity), an extension method (a shadowing one is not
///     the bound one), type inference, a conversion or an operator, a constructor, a declaration that was added or
///     removed, a using, and a file with a broad change. It also declines an analyzer error, an error whose symbol
///     is declared in a file no session is credited with, and an error traced sessions wrote together, because a
///     view, not a trace, decides those.
/// </remarks>
internal sealed class DeclarationFilter
{
    /// <summary>
    ///     The errors whose answer can depend on a conversion, an overload or an inference the site does not bind to
    ///     one symbol, by diagnostic id. The view decides each of them.
    /// </summary>
    private static readonly HashSet<string> DeclinedErrorIds =
    [
        "CS0029", "CS0030", "CS0034", "CS0266", "CS0411", "CS0413", "CS0121", "CS1501", "CS1502", "CS1503",
        "CS1660", "CS1661", "CS1667", "CS1729", "CS7036",
    ];

    private const int MaxCachedFiles = 256;

    private readonly RepoWorkspace _workspace;
    private readonly SessionEdits _edits;
    private readonly ConcurrentDictionary<(int BaselineGeneration, RepoPath Path, CSharpParseOptions? Options), FileDeclarations> _headDeclarations = new();
    private readonly ConcurrentDictionary<(int BaselineGeneration, DocumentId DocId, string Checksum), CachedSurfaceDiff> _surfaceDiffs = new();
    private readonly Lock _cacheLock = new();
    private int _cachedBaselineGeneration = -1;
    private int _headParses;

    public DeclarationFilter(RepoWorkspace workspace, SessionEdits edits)
    {
        _workspace = workspace;
        _edits = edits;
    }

    /// <summary>How many times HEAD syntax was parsed on a cache miss; tests read this to verify caching.</summary>
    internal int HeadParses => Volatile.Read(ref _headParses);

    /// <summary>What the filter makes of one error for one session: told to it, left to another session, or declined to the view.</summary>
    internal enum Decision
    {
        Declined,
        Told,
        NotTold,
    }

    /// <summary>
    ///     What <paramref name="session"/> is told about <paramref name="error"/> of <paramref name="generation"/>,
    ///     decided from the declaration trace alone, or <see cref="Decision.Declined"/> when the trace does not
    ///     decide it and the session view must.
    /// </summary>
    /// <param name="error">One error the check found in the tree view.</param>
    /// <param name="session">The session the check is answered to.</param>
    /// <param name="generation">The generation the check pinned, whose tree view binds the error.</param>
    /// <param name="writers">The sessions that wrote each changed file of <paramref name="generation"/>.</param>
    /// <param name="cancellationToken">Cancels the binding the trace reads.</param>
    public async Task<Decision> DecideAsync(CompilerError error, string session, Generation generation, IReadOnlyDictionary<RepoPath, IReadOnlySet<string>> writers, CancellationToken cancellationToken)
    {
        if (error.FromAnalyzer || DeclinedErrorIds.Contains(error.Id))
            return Decision.Declined;
        var root = _workspace.Root;
        var errorPath = root.PathOf(error.Path);
        var current = generation.Current;
        var ids = current.GetDocumentIdsWithFilePath(errorPath.Absolute);
        if (ids.Length != 1 || current.GetDocument(ids[0]) is not { } document)
            return Decision.Declined;
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (tree is null || model is null)
            return Decision.Declined;
        var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (error.Line < 1 || error.Line > text.Lines.Count)
            return Decision.Declined;
        var line = text.Lines[error.Line - 1];
        var position = Math.Min(line.Start + Math.Max(0, error.Column - 1), Math.Max(line.Start, line.End - 1));
        var syntaxRoot = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // The token at the error and the few expressions around it, as GeneratedSource reads a site: a name, its
        // member access, the call that holds it. Every symbol and candidate found there must be the one symbol.
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var node = syntaxRoot.FindToken(position).Parent;
        for (var depth = 0; node is not null && depth < 4; depth++, node = node.Parent)
        {
            var info = model.GetSymbolInfo(node, cancellationToken);
            if (info.Symbol is { } bound && !IsSiteLocal(bound))
                symbols.Add(bound);
            foreach (var candidate in info.CandidateSymbols)
            {
                if (!IsSiteLocal(candidate))
                    symbols.Add(candidate);
            }
            if (node is StatementSyntax or MemberDeclarationSyntax or InvocationExpressionSyntax)
                break;
        }

        if (symbols.Count != 1)
            return Decision.Declined;
        var symbol = symbols.Single();
        if (symbol.IsImplicitlyDeclared || !IsDecidable(symbol))
            return Decision.Declined;
        if (symbol.DeclaringSyntaxReferences.Length != 1)
            return Decision.Declined;
        var declaredNode = await symbol.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
        var declaredFile = symbol.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(declaredFile) || !Path.IsPathRooted(declaredFile))
            return Decision.Declined;
        var declaringPath = root.PathOf(declaredFile);
        if (await ChangedDeclarationAsync(generation, document, declaringPath, declaredNode, writers, cancellationToken).ConfigureAwait(false) is not { } sessionsOfFile)
            return Decision.Declined;

        // The site itself is the other half of the trace: a session that wrote the file the error is in can have
        // made the error together with the session that changed the declaration, so its sessions count too. A
        // file no session wrote is the tree every session builds on and counts for none.
        var sessions = new HashSet<string>(sessionsOfFile, StringComparer.Ordinal);
        if (!errorPath.Equals(declaringPath) && writers.TryGetValue(errorPath, out var siteWriters))
            sessions.UnionWith(siteWriters);
        if (sessions.Count == 0)
            return Decision.Declined;
        if (sessions.All(w => _edits.IsOwnEdit(w, session)))
            return Decision.Told;
        if (sessions.Count != 1)
            return Decision.Declined;
        // The one session is another live session: its edits are the ones a session view holds at HEAD, so an error
        // they alone cause is not told.
        return Decision.NotTold;
    }

    /// <summary>
    ///     True for a symbol the site itself declares, a local, a parameter or a range variable: it is part of the
    ///     site, not a declaration another file changed, so the trace leaves it out.
    /// </summary>
    private static bool IsSiteLocal(ISymbol symbol) => symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or IDiscardSymbol;

    /// <summary>True for a symbol a trace can decide: no constructor, conversion, operator or extension method among them.</summary>
    private static bool IsDecidable(ISymbol symbol) =>
        symbol is not IMethodSymbol method
        || method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Conversion or MethodKind.UserDefinedOperator)
            && method.ReducedFrom is null;

    /// <summary>
    ///     The sessions that wrote the file <paramref name="declaringPath"/> names, when the declaration
    ///     <paramref name="declaredNode"/> declares there is a changed declaration of that file and nothing in the
    ///     file makes the trace unsafe; null when the filter declines the file. A file with a broad change, a using
    ///     change, an added or removed declaration, or a changed constructor, conversion or operator declines, as
    ///     does a changed declaration in a file no session is credited with.
    /// </summary>
    private async Task<IReadOnlySet<string>?> ChangedDeclarationAsync(Generation generation, Document errorDocument, RepoPath declaringPath, SyntaxNode declaredNode, IReadOnlyDictionary<RepoPath, IReadOnlySet<string>> writers, CancellationToken cancellationToken)
    {
        var current = generation.Current;
        Document? declaringDocument = null;
        foreach (var id in current.GetDocumentIdsWithFilePath(declaringPath.Absolute))
        {
            if (current.GetDocument(id) is not { } candidate)
                continue;
            declaringDocument ??= candidate;
            if (candidate.Project.Id == errorDocument.Project.Id)
            {
                declaringDocument = candidate;
                break;
            }
        }

        if (declaringDocument is null)
            return null;
        if (await declaringDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } afterRoot)
            return null;

        var afterText = await declaringDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var checksum = Convert.ToHexString(afterText.GetChecksum().AsSpan());
        var baselineGen = generation.BaselineGeneration;
        ForgetOlderGeneration(baselineGen);

        var cacheKey = (baselineGen, declaringDocument.Id, checksum);
        if (!_surfaceDiffs.TryGetValue(cacheKey, out var cached) || !ReferenceEquals(cached.AfterRoot, afterRoot))
        {
            var before = await HeadDeclarationsAsync(generation, declaringDocument, declaringPath, cancellationToken).ConfigureAwait(false);
            if (before is null)
                return null;
            var after = FileDeclarations.Of(afterRoot);
            var changes = SurfaceDiff.Compare(before, after);
            var safe = IsSafeSurfaceFile(changes, before, after);
            var changedParts = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
            if (safe)
            {
                var changedKeys = changes.Changes.Select(c => c.Key).ToHashSet();
                foreach (var declaration in after.All)
                {
                    if (!changedKeys.Contains(declaration.Key))
                        continue;
                    foreach (var part in declaration.Parts)
                        changedParts.Add(part);
                }
            }

            cached = new CachedSurfaceDiff(afterRoot, safe, changedParts);
            lock (_cacheLock)
            {
                if (_surfaceDiffs.Count >= MaxCachedFiles)
                    _surfaceDiffs.Clear();
                _surfaceDiffs[cacheKey] = cached;
            }
        }

        if (!cached.IsSafeFile || !cached.ChangedParts.Contains(declaredNode))
            return null;
        return writers.TryGetValue(declaringPath, out var sessions) && sessions.Count > 0 ? sessions : null;
    }

    private async Task<FileDeclarations?> HeadDeclarationsAsync(Generation generation, Document declaringDocument, RepoPath declaringPath, CancellationToken cancellationToken)
    {
        var parseOptions = declaringDocument.Project.ParseOptions as CSharpParseOptions;
        var key = (generation.BaselineGeneration, declaringPath, parseOptions);
        if (_headDeclarations.TryGetValue(key, out var cached))
            return cached;

        FileDeclarations? parsed = null;
        if (generation.Baseline.GetDocument(declaringDocument.Id) is { } baselineDoc
            && await baselineDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } baselineRoot)
        {
            parsed = FileDeclarations.Of(baselineRoot);
        }
        else if (_workspace.HeadText(declaringPath) is { } head)
        {
            Interlocked.Increment(ref _headParses);
            var options = parseOptions ?? CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var beforeRoot = await CSharpSyntaxTree
                .ParseText(head, options, cancellationToken: cancellationToken)
                .GetRootAsync(cancellationToken).ConfigureAwait(false);
            parsed = FileDeclarations.Of(beforeRoot);
        }

        if (parsed is null)
            return null;

        lock (_cacheLock)
        {
            if (_headDeclarations.Count >= MaxCachedFiles)
                _headDeclarations.Clear();
            _headDeclarations[key] = parsed;
        }

        return parsed;
    }

    private void ForgetOlderGeneration(int baselineGeneration)
    {
        if (baselineGeneration == Volatile.Read(ref _cachedBaselineGeneration))
            return;
        lock (_cacheLock)
        {
            if (baselineGeneration == _cachedBaselineGeneration)
                return;
            _headDeclarations.Clear();
            _surfaceDiffs.Clear();
            _cachedBaselineGeneration = baselineGeneration;
        }
    }

    private static bool IsSafeSurfaceFile(FileChanges changes, FileDeclarations before, FileDeclarations after)
    {
        if (changes.HasBroadChange || changes.Changes.Count == 0)
            return false;
        foreach (var change in changes.Changes)
        {
            if (change is not DeclarationChange.Changed || change.Key is DeclarationKey.Using or DeclarationKey.GlobalUsing or DeclarationKey.AssemblyAttribute)
                return false;
            if ((after.Find(change.Key) ?? before.Find(change.Key))?.Node is ConstructorDeclarationSyntax or ConversionOperatorDeclarationSyntax or OperatorDeclarationSyntax)
                return false;
        }

        return true;
    }

    private sealed record CachedSurfaceDiff(SyntaxNode AfterRoot, bool IsSafeFile, HashSet<SyntaxNode> ChangedParts);
}
