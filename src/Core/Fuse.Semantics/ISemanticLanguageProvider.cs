using Fuse.Semantics.Analyzers;

namespace Fuse.Semantics;

/// <summary>
///     A language provider at the semantic tier: it supplies the typed-graph analyzers that run over a loaded
///     compilation and produce wiring edges, routes, DI registrations, and options bindings. This is the seam
///     that makes the semantic indexer provider-driven rather than hardwired to one language, so a second
///     language drops in by registering a provider without changing the shared runner.
/// </summary>
/// <remarks>
///     The semantic tier is optional and compiler-backed: unlike <see cref="ILanguageSyntaxProvider" />, which
///     extracts symbols without a compiler, a semantic provider's analyzers require a loaded project compilation.
///     Only C# ships in 4.2; the F6 entry bar still applies before a second first-party semantic language.
/// </remarks>
public interface ISemanticLanguageProvider
{
    /// <summary>The provider's language id (for example <c>csharp</c>).</summary>
    string Language { get; }

    /// <summary>
    ///     The analyzers this provider registers for its language, in run order. Order matters where one
    ///     analyzer consumes another's output (for example constructor injection after DI registration).
    /// </summary>
    IReadOnlyList<ISemanticAnalyzer> Analyzers { get; }

    /// <summary>
    ///     Creates a runner that executes only this provider's analyzers.
    /// </summary>
    /// <returns>A runner wired with <see cref="Analyzers" />.</returns>
    SemanticAnalysisRunner CreateRunner() => new(Analyzers);
}

/// <summary>
///     Selects a <see cref="ISemanticLanguageProvider" /> by language id and builds per-language analysis
///     runners from the registered providers.
/// </summary>
public sealed class SemanticLanguageProviderRegistry
{
    private readonly Dictionary<string, ISemanticLanguageProvider> _byLanguage;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticLanguageProviderRegistry" /> class.
    /// </summary>
    /// <param name="providers">The providers to register; a later provider wins a contested language id.</param>
    public SemanticLanguageProviderRegistry(IEnumerable<ISemanticLanguageProvider> providers)
    {
        _byLanguage = new Dictionary<string, ISemanticLanguageProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            _byLanguage[provider.Language] = provider;
    }

    /// <summary>The language ids of every registered provider.</summary>
    public IReadOnlyCollection<string> Languages => _byLanguage.Keys;

    /// <summary>
    ///     Returns the provider for a language, or null when none is registered.
    /// </summary>
    /// <param name="language">The language id (for example <c>csharp</c>).</param>
    /// <returns>The matching provider, or null.</returns>
    public ISemanticLanguageProvider? ForLanguage(string language) => _byLanguage.GetValueOrDefault(language);

    /// <summary>
    ///     Creates a runner for the named language's analyzers.
    /// </summary>
    /// <param name="language">The language id.</param>
    /// <returns>A runner wired with that provider's analyzers.</returns>
    /// <exception cref="InvalidOperationException">No provider is registered for <paramref name="language" />.</exception>
    public SemanticAnalysisRunner RunnerFor(string language)
    {
        if (!_byLanguage.TryGetValue(language, out var provider))
            throw new InvalidOperationException($"No semantic language provider is registered for '{language}'.");
        return provider.CreateRunner();
    }
}

/// <summary>
///     The C# semantic provider: the shipped wiring analyzers behind the language-provider seam, so the C# path
///     is unchanged but is now selected generically by language like any future semantic language.
/// </summary>
public sealed class CSharpSemanticLanguageProvider : ISemanticLanguageProvider
{
    /// <inheritdoc />
    public string Language => "csharp";

    /// <inheritdoc />
    public IReadOnlyList<ISemanticAnalyzer> Analyzers { get; } = CreateAnalyzers();

    /// <inheritdoc />
    public SemanticAnalysisRunner CreateRunner() => new(Analyzers);

    /// <summary>
    ///     Builds the standard C# analyzer set (interface, DI, constructor injection, MediatR, route, options,
    ///     hosted services, pipeline behaviors, EF Core, endpoints, references).
    /// </summary>
    /// <returns>The analyzers in run order.</returns>
    internal static IReadOnlyList<ISemanticAnalyzer> CreateAnalyzers()
    {
        var di = new DiRegistrationAnalyzer();
        return
        [
            new InterfaceImplementationAnalyzer(),
            di,
            new ConstructorInjectionAnalyzer(di),
            new MediatRAnalyzer(),
            new AspNetRouteAnalyzer(),
            new OptionsBindingAnalyzer(),
            new HostedServiceAnalyzer(),
            new PipelineBehaviorAnalyzer(),
            new EfCoreAnalyzer(),
            new EndpointAnalyzer(),
            new ReferenceEdgeAnalyzer(),
        ];
    }
}
