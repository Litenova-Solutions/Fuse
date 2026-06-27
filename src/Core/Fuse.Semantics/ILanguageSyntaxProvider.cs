using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     A language provider at the syntax tier: it claims a set of file extensions and extracts symbols and
///     chunks from a file using lexical or syntax analysis only, with no compiler. This is the seam that makes
///     the indexer provider-driven rather than hardwired to one language, so a second language drops in by
///     registering a provider without changing the shared indexer.
/// </summary>
/// <remarks>
///     The syntax tier is the floor every language reaches: it produces the symbols and chunks that back
///     full-text search and localization. A semantic tier (a typed-graph builder backed by a compiler) is a
///     separate, optional capability layered above this seam; this interface deliberately covers only the
///     compiler-free syntax extraction so a provider can be a bounded lexer.
/// </remarks>
public interface ILanguageSyntaxProvider
{
    /// <summary>The provider's language id (for example <c>csharp</c> or <c>python</c>).</summary>
    string Language { get; }

    /// <summary>The file extensions this provider claims, each including the leading dot (for example <c>.cs</c>).</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>
    ///     Extracts the symbols and chunks declared in a file.
    /// </summary>
    /// <param name="normalizedPath">The forward-slash, repo-relative path used to key the records to the file.</param>
    /// <param name="content">The file's source text.</param>
    /// <returns>The extracted symbols and chunks; empty when the file declares nothing or fails to parse.</returns>
    SyntaxExtractionResult Extract(string normalizedPath, string content);
}

/// <summary>
///     Selects a <see cref="ILanguageSyntaxProvider" /> by file extension, and reports the union of all claimed
///     extensions so the file scanner can be driven by the registered providers rather than a hardwired set.
/// </summary>
public sealed class LanguageSyntaxProviderRegistry
{
    private readonly Dictionary<string, ILanguageSyntaxProvider> _byExtension;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LanguageSyntaxProviderRegistry" /> class.
    /// </summary>
    /// <param name="providers">The providers to register; a later provider wins a contested extension.</param>
    public LanguageSyntaxProviderRegistry(IEnumerable<ILanguageSyntaxProvider> providers)
    {
        _byExtension = new Dictionary<string, ILanguageSyntaxProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var extension in provider.Extensions)
                _byExtension[extension] = provider;
        }
    }

    /// <summary>The union of every registered provider's claimed extensions.</summary>
    public IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    /// <summary>
    ///     Returns the provider that claims an extension, or null when none does.
    /// </summary>
    /// <param name="extension">The file extension, including the leading dot.</param>
    /// <returns>The matching provider, or null.</returns>
    public ILanguageSyntaxProvider? ForExtension(string extension) => _byExtension.GetValueOrDefault(extension);
}

/// <summary>
///     The C# syntax provider: the existing Roslyn-syntax symbol and chunk extractor behind the language-provider
///     seam, so the C# path is unchanged but is now selected generically by extension like any other language.
/// </summary>
public sealed class CSharpSyntaxProvider : ILanguageSyntaxProvider
{
    private readonly SyntaxSymbolExtractor _extractor;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CSharpSyntaxProvider" /> class.
    /// </summary>
    /// <param name="extractor">The C# syntax symbol and chunk extractor to delegate to.</param>
    public CSharpSyntaxProvider(SyntaxSymbolExtractor extractor) => _extractor = extractor;

    /// <inheritdoc />
    public string Language => "csharp";

    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".cs"];

    /// <inheritdoc />
    public SyntaxExtractionResult Extract(string normalizedPath, string content) => _extractor.Extract(normalizedPath, content);
}
