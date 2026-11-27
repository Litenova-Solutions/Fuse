using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Check.Enrichment;

/// <summary>
///     Builds compact, Roslyn-free <see cref="RepairPacket"/> hints from bound compiler diagnostics so agents can
///     fix common errors without reading other files.
/// </summary>
internal static class RepairPackets
{
    /// <summary>The most candidates or using directives one packet names.</summary>
    internal const int MaxSuggestions = 5;

    /// <summary>How members print in a packet: minimal qualification with full signatures.</summary>
    internal static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithParameterOptions(
                SymbolDisplayParameterOptions.IncludeExtensionThis
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                | SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeDefaultValue)
            .WithGenericsOptions(
                SymbolDisplayGenericsOptions.IncludeTypeParameters
                | SymbolDisplayGenericsOptions.IncludeTypeConstraints)
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    ///     Builds a deterministic <see cref="RepairPacket"/> for <paramref name="diagnostic"/> using
    ///     <paramref name="model"/>, <paramref name="compilation"/>, and <paramref name="text"/>.
    /// </summary>
    public static RepairPacket Build(
        Diagnostic diagnostic,
        SemanticModel model,
        Compilation compilation,
        SourceText text,
        Solution? solution = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(text);

        var lineSpan = diagnostic.Location.GetLineSpan();
        var errorLine = Math.Clamp(lineSpan.StartLinePosition.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var sourceWindow = BuildSourceWindow(text, errorLine);

        var root = model.SyntaxTree.GetRoot(cancellationToken);
        var span = diagnostic.Location.SourceSpan;
        var token = FindTokenAt(root, span.Start);
        var within = EnclosingWithin(model, token, compilation, cancellationToken);

        IReadOnlyList<string> candidates = [];
        IReadOnlyList<string> suggestedUsings = [];
        string? interfaceStub = null;

        switch (diagnostic.Id)
        {
            case "CS1061":
            case "CS0117":
                candidates = MemberCandidateLookup.MissingMemberCandidates(diagnostic, model, compilation, within, token, span, cancellationToken);
                break;
            case "CS1503":
            case "CS7036":
            case "CS1501":
            case "CS1729":
                candidates = MemberCandidateLookup.OverloadCandidates(model, compilation, within, token, cancellationToken);
                break;
            case "CS0246":
                suggestedUsings = UsingSuggestionLookup.MissingTypeUsings(diagnostic, model, compilation, within, token, solution, cancellationToken);
                break;
            case "CS0535":
                interfaceStub = InterfaceStubGenerator.UnimplementedInterfaceStub(diagnostic, model, token, cancellationToken);
                break;
        }

        return new RepairPacket(sourceWindow, candidates, suggestedUsings, interfaceStub);
    }

    /// <summary>
    ///     Attaches <see cref="RepairPacket"/> hints to each error in <paramref name="errors"/> from
    ///     <paramref name="solution"/>, grouping lookups by file.
    /// </summary>
    public static async Task<IReadOnlyList<IntroducedError>> AttachAsync(
        Solution solution,
        RepoRoot root,
        IReadOnlyList<IntroducedError> errors,
        CancellationToken cancellationToken)
    {
        if (errors.Count == 0)
            return errors;

        var documents = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        var snapshots = new Dictionary<DocumentId, DocumentSnapshot?>();
        var result = new List<IntroducedError>(errors.Count);

        foreach (var entry in errors)
        {
            var error = entry.Error;
            if (!documents.TryGetValue(error.Path, out var candidates))
            {
                candidates = DocumentsOf(solution, root, error.Path);
                documents[error.Path] = candidates;
            }

            // A file shared by several target frameworks has one document per framework, and code inside an #if
            // branch binds in only some of them: the packet comes from the first document that has the error.
            RepairPacket? packet = null;
            DocumentSnapshot? first = null;
            foreach (var document in candidates)
            {
                if (!snapshots.TryGetValue(document.Id, out var snapshot))
                {
                    snapshot = await LoadSnapshotAsync(document, cancellationToken).ConfigureAwait(false);
                    snapshots[document.Id] = snapshot;
                }

                if (snapshot is null)
                    continue;
                first ??= snapshot;
                packet = BuildForError(error, snapshot, solution, cancellationToken);
                if (packet is not null)
                    break;
            }

            if (first is null)
            {
                result.Add(entry);
                continue;
            }

            packet ??= new RepairPacket(BuildSourceWindow(first.Text, LineIndexOf(error, first.Text)), [], [], null);
            result.Add(entry with { Repair = packet, Error = error with { Repair = packet } });
        }

        return result;
    }

    private static List<Document> DocumentsOf(Solution solution, RepoRoot root, string relativePath)
    {
        var absolute = root.PathOf(relativePath).Absolute;
        var found = new List<Document>();
        foreach (var id in solution.GetDocumentIdsWithFilePath(absolute))
        {
            if (solution.GetDocument(id) is { } document)
                found.Add(document);
        }

        return found;
    }

    private static async Task<DocumentSnapshot?> LoadSnapshotAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return model is null ? null : new DocumentSnapshot(model, model.Compilation, text);
    }

    private static int LineIndexOf(CompilerError error, SourceText text) => Math.Clamp(error.Line - 1, 0, Math.Max(0, text.Lines.Count - 1));

    /// <summary>The packet built from the diagnostic in <paramref name="snapshot"/> that matches the error, or null when the document has none.</summary>
    private static RepairPacket? BuildForError(
        CompilerError error,
        DocumentSnapshot snapshot,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (error.FromAnalyzer || error.Line < 1 || error.Line > snapshot.Text.Lines.Count)
            return null;
        var lineSpan = snapshot.Text.Lines[LineIndexOf(error, snapshot.Text)].Span;
        var diagnostics = snapshot.Model.GetDiagnostics(lineSpan, cancellationToken);
        var matched = diagnostics.FirstOrDefault(d => Matches(d, error))
            ?? diagnostics.FirstOrDefault(d => d.Id == error.Id && d.Location.GetMappedLineSpan().StartLinePosition.Line + 1 == error.Line);
        return matched is null ? null : Build(matched, snapshot.Model, snapshot.Compilation, snapshot.Text, solution, cancellationToken);
    }

    private static bool Matches(Diagnostic diagnostic, CompilerError error)
    {
        if (!string.Equals(diagnostic.Id, error.Id, StringComparison.Ordinal))
            return false;
        var span = diagnostic.Location.GetMappedLineSpan();
        return span.StartLinePosition.Line + 1 == error.Line
            && span.StartLinePosition.Character + 1 == error.Column;
    }

    private static List<string> BuildSourceWindow(SourceText text, int errorLineIndex)
    {
        if (text.Lines.Count == 0)
            return [];
        var start = Math.Max(0, errorLineIndex - 2);
        var end = Math.Min(text.Lines.Count - 1, errorLineIndex + 2);
        var window = new List<string>(end - start + 1);
        for (var i = start; i <= end; i++)
        {
            var prefix = i == errorLineIndex ? "> " : "  ";
            var content = text.Lines[i].ToString().TrimEnd('\r');
            window.Add($"{prefix}{i + 1}: {content}");
        }

        return window;
    }

    private static SyntaxToken FindTokenAt(SyntaxNode root, int position)
    {
        var clamped = Math.Clamp(position, root.FullSpan.Start, Math.Max(root.FullSpan.Start, root.FullSpan.End - 1));
        return root.FindToken(clamped);
    }

    private static ISymbol EnclosingWithin(SemanticModel model, SyntaxToken token, Compilation compilation, CancellationToken cancellationToken)
    {
        var typeSyntax = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeSyntax is not null && model.GetDeclaredSymbol(typeSyntax, cancellationToken) is { } typeSymbol)
            return typeSymbol;
        return compilation.Assembly;
    }

    /// <summary>The first quoted term in a diagnostic message, such as the type name in a CS0246 message.</summary>
    internal static string? FirstQuotedTerm(string message) => QuotedTerm(message, 0);

    /// <summary>The second quoted term in a diagnostic message, such as the member name in a CS1061 message.</summary>
    internal static string? SecondQuotedTerm(string message) => QuotedTerm(message, 1);

    private static string? QuotedTerm(string message, int index)
    {
        var searchStart = 0;
        for (var i = 0; i <= index; i++)
        {
            var open = message.IndexOf('\'', searchStart);
            if (open < 0)
                return null;
            var close = message.IndexOf('\'', open + 1);
            if (close < 0)
                return null;
            if (i == index)
                return message[(open + 1)..close];
            searchStart = close + 1;
        }

        return null;
    }

    private sealed record DocumentSnapshot(SemanticModel Model, Compilation Compilation, SourceText Text);
}
