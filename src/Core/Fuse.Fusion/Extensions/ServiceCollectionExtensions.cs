using Fuse.Fusion.Scoping;
using Fuse.Fusion.Enrichment;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Templates;
using Fuse.Collection.Templates.Definitions;
using Fuse.Emission;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Plugins.Formats.Web.Extensions;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Plugins.Languages.CSharp.Extensions;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Extensions;

/// <summary>
///     Extension methods for registering Fuse fusion services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers all Fuse fusion services required to execute the collection, reduction, and emission pipelines,
    ///     including the C# language plugin and built-in format reducers.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    public static IServiceCollection AddFuse(this IServiceCollection services)
    {
        services.AddFuseCore();
        services.AddCSharpLanguage();
        services.AddFormatReducers();
        return services;
    }

    /// <summary>
    ///     Registers the core fusion pipelines, analysis engine, capability registries, file filters, and project
    ///     templates, without any language-specific plugins.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <paramref name="services" /> instance, to allow chaining.</returns>
    /// <remarks>
    ///     Use <see cref="AddFuse" /> for the full default registration. Call this directly only when composing a
    ///     custom set of language plugins on top of the core services. File filter registration order equals
    ///     evaluation order.
    /// </remarks>
    public static IServiceCollection AddFuseCore(this IServiceCollection services)
    {
        services.AddSingleton<TokenizerFactory>();
        services.AddSingleton<ITokenCounter>(sp => sp.GetRequiredService<TokenizerFactory>().GetCounter());
        services.AddSingleton<IEntryFormatter, XmlEntryFormatter>();
        services.AddSingleton<FusionValidator>();
        services.AddSingleton<FusionOrchestrator>();
        services.AddSingleton<ISecretRedactor, DefaultSecretRedactor>();
        // Per-run relevance index: the BM25 index rebuilds all of its state per query and shares nothing, so a
        // factory hands each fusion run a fresh instance instead of a serialized singleton.
        services.AddSingleton<Func<IRelevanceIndex>>(_ => () => new Bm25RelevanceIndex());
        services.AddSingleton<Retrieval.IEmbeddingModel>(_ => new Retrieval.HashingEmbeddingModel());

        services.AddTransient<FileCollectionPipeline>();
        services.AddTransient<ContentReductionPipeline>();
        services.AddTransient<EmissionPipeline>();

        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        // Per-run content cache: a fresh instance per run keeps the read-once-per-run invariant intact under
        // concurrent runs (a shared cache would be cleared mid-flight by another run).
        services.AddSingleton<Func<ISourceContentProvider>>(sp =>
            () => new SourceContentProvider(sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<GitIgnoreParser>();
        services.AddSingleton<ProjectTemplateRegistry>();
        services.AddSingleton<OutputNamingService>();

        services.AddSingleton(sp => new CapabilityRegistry<IContentReducer>(sp.GetServices<IContentReducer>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISkeletonExtractor>(sp.GetServices<ISkeletonExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolOutlineExtractor>(sp.GetServices<ISymbolOutlineExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolSliceExtractor>(sp.GetServices<ISymbolSliceExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISymbolChunkExtractor>(sp.GetServices<ISymbolChunkExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ISemanticMarkerGenerator>(sp.GetServices<ISemanticMarkerGenerator>()));
        services.AddSingleton(sp => new CapabilityRegistry<IDependencyExtractor>(sp.GetServices<IDependencyExtractor>()));
        services.AddSingleton(sp => new CapabilityRegistry<ITypeNameLocator>(sp.GetServices<ITypeNameLocator>()));

        services.AddSingleton<DependencyGraphBuilder>();
        services.AddSingleton<FocusSeedResolver>();
        services.AddSingleton<Enrichment.BoilerplateDeduplicator>();
        services.AddSingleton<Enrichment.BodyDeduplicator>();

        services.AddSingleton<Session.ISessionTracker, Session.InMemorySessionTracker>();
        services.AddSingleton<IChangeDetector, GitChangeDetector>();
        services.AddSingleton<IGitStatsProvider, GitStatsProvider>();
        services.AddSingleton<IFuseStoreFactory, FuseStoreFactory>();

        RegisterFileFilters(services);
        RegisterProjectTemplates(services);

        return services;
    }

    private static void RegisterFileFilters(IServiceCollection services)
    {
        services.AddTransient<IFileFilter, GitIgnoreFilter>();
        services.AddTransient<IFileFilter, ExtensionFilter>();
        services.AddTransient<IFileFilter, ExcludedDirectoryFilter>();
        services.AddTransient<IFileFilter, TestProjectFilter>();
        services.AddTransient<IFileFilter, UnitTestProjectFilter>();
        services.AddTransient<IFileFilter, FileSizeFilter>();
        services.AddTransient<IFileFilter, BinaryFileFilter>();
        services.AddTransient<IFileFilter, EmptyFileFilter>();
        services.AddTransient<IFileFilter, AutoGeneratedFileFilter>();
        services.AddTransient<IFileFilter, ExcludedFileNameFilter>();
        services.AddTransient<IFileFilter, GlobPatternFilter>();
    }

    private static void RegisterProjectTemplates(IServiceCollection services)
    {
        services.AddSingleton<IProjectTemplate, AzureDevOpsWikiTemplate>();
        services.AddSingleton<IProjectTemplate, ClojureTemplate>();
        services.AddSingleton<IProjectTemplate, CppCSharpTemplate>();
        services.AddSingleton<IProjectTemplate, DartTemplate>();
        services.AddSingleton<IProjectTemplate, DotNetTemplate>();
        services.AddSingleton<IProjectTemplate, ElixirTemplate>();
        services.AddSingleton<IProjectTemplate, ErlangTemplate>();
        services.AddSingleton<IProjectTemplate, FsharpTemplate>();
        services.AddSingleton<IProjectTemplate, GenericTemplate>();
        services.AddSingleton<IProjectTemplate, GoTemplate>();
        services.AddSingleton<IProjectTemplate, HaskellTemplate>();
        services.AddSingleton<IProjectTemplate, InfrastructureTemplate>();
        services.AddSingleton<IProjectTemplate, JavaScriptTemplate>();
        services.AddSingleton<IProjectTemplate, JavaTemplate>();
        services.AddSingleton<IProjectTemplate, KotlinTemplate>();
        services.AddSingleton<IProjectTemplate, LuaTemplate>();
        services.AddSingleton<IProjectTemplate, PerlTemplate>();
        services.AddSingleton<IProjectTemplate, PhpTemplate>();
        services.AddSingleton<IProjectTemplate, PythonTemplate>();
        services.AddSingleton<IProjectTemplate, RTemplate>();
        services.AddSingleton<IProjectTemplate, RubyTemplate>();
        services.AddSingleton<IProjectTemplate, RustTemplate>();
        services.AddSingleton<IProjectTemplate, ScalaTemplate>();
        services.AddSingleton<IProjectTemplate, SwiftTemplate>();
        services.AddSingleton<IProjectTemplate, TypeScriptTemplate>();
        services.AddSingleton<IProjectTemplate, VbNetTemplate>();
    }
}
