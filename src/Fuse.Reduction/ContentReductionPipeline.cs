using System.Text;

using Fuse.Collection.FileSystem;

using Fuse.Collection.Models;

using Fuse.Reduction.Markers;

using Fuse.Reduction.Models;

using Fuse.Reduction.Options;

using Fuse.Reduction.Reducers;

using Fuse.Reduction.Skeleton;

using Fuse.Reduction.Tokenization;



namespace Fuse.Reduction;



/// <summary>

///     Reads source files, normalizes whitespace, applies extension-specific reducers,

///     and returns non-trivial fused content for emission.

/// </summary>

public sealed class ContentReductionPipeline

{

    private readonly IFileSystem _fileSystem;

    private readonly ReducerRegistry _reducerRegistry;

    private readonly SkeletonExtractorRegistry _skeletonExtractorRegistry;

    private readonly SemanticMarkerGeneratorRegistry _semanticMarkerGeneratorRegistry;

    private readonly ITokenCounter _tokenCounter;



    /// <summary>

    ///     Initializes a new instance of the <see cref="ContentReductionPipeline" /> class.

    /// </summary>

    public ContentReductionPipeline(

        IFileSystem fileSystem,

        ReducerRegistry reducerRegistry,

        SkeletonExtractorRegistry skeletonExtractorRegistry,

        SemanticMarkerGeneratorRegistry semanticMarkerGeneratorRegistry,

        ITokenCounter tokenCounter)

    {

        _fileSystem = fileSystem;

        _reducerRegistry = reducerRegistry;

        _skeletonExtractorRegistry = skeletonExtractorRegistry;

        _semanticMarkerGeneratorRegistry = semanticMarkerGeneratorRegistry;

        _tokenCounter = tokenCounter;

    }



    /// <summary>

    ///     Reduces the supplied source files and returns non-trivial fused content.

    /// </summary>

    public async Task<IReadOnlyList<FusedContent>> ReduceAsync(

        IReadOnlyList<SourceFile> sourceFiles,

        ReductionOptions options,

        CancellationToken cancellationToken = default)

    {

        var results = new List<FusedContent>(sourceFiles.Count);



        foreach (var sourceFile in sourceFiles)

        {

            cancellationToken.ThrowIfCancellationRequested();



            var content = await _fileSystem.ReadAllTextAsync(sourceFile.FullPath, cancellationToken);

            content = NormalizeWhitespace(content, options);

            content = ApplyReduction(content, sourceFile.Extension, options);

            content = ApplySkeleton(content, sourceFile, options);

            content = ApplySemanticMarkers(content, sourceFile, options);



            var fused = new FusedContent(sourceFile, content, _tokenCounter);



            if (!fused.IsTrivial)

                results.Add(fused);

        }



        return results;

    }



    private string ApplySkeleton(string content, SourceFile sourceFile, ReductionOptions options)

    {

        if (!options.SkeletonMode || !sourceFile.IsCSharp)

            return content;



        var extractor = _skeletonExtractorRegistry.TryGetExtractor(sourceFile.Extension);

        return extractor?.ExtractSkeleton(content) ?? content;

    }



    private string ApplySemanticMarkers(string content, SourceFile sourceFile, ReductionOptions options)

    {

        if (!options.IncludeSemanticMarkers || !sourceFile.IsCSharp)

            return content;



        var generator = _semanticMarkerGeneratorRegistry.TryGetGenerator(sourceFile.Extension);

        if (generator is null)

            return content;



        var markers = generator.GenerateMarkers(content);

        if (markers.Count == 0)

            return content;



        var sb = new StringBuilder();

        foreach (var marker in markers)

            sb.AppendLine(marker.ToComment());

        sb.Append(content);

        return sb.ToString();

    }



    private static string NormalizeWhitespace(string content, ReductionOptions options)

    {

        if (options.TrimContent)

        {

            content = System.Text.RegularExpressions.Regex.Replace(content, @"^[\s\t]+|[\s\t]+$", string.Empty,

                System.Text.RegularExpressions.RegexOptions.Multiline);

        }



        if (options.UseCondensing)

        {

            content = System.Text.RegularExpressions.Regex.Replace(content, @"^\s*$\r?\n", string.Empty,

                System.Text.RegularExpressions.RegexOptions.Multiline);

        }



        return content;

    }



    private string ApplyReduction(string content, string extension, ReductionOptions options)

    {

        if (!ShouldReduce(extension, options))

            return content;



        var reducer = _reducerRegistry.TryGetReducer(extension);

        return reducer?.Reduce(content, options) ?? content;

    }



    private static bool ShouldReduce(string extension, ReductionOptions options)

    {

        return extension switch

        {

            ".cs" => true,

            ".razor" => true,

            ".cshtml" => options.MinifyHtmlAndRazor,

            ".html" or ".htm" => options.MinifyHtmlAndRazor,

            ".css" or ".scss" or ".js" or ".json" or ".md" or ".yaml" or ".yml" => true,

            ".xml" or ".targets" or ".props" or ".csproj" => options.MinifyXmlFiles,

            _ => false,

        };

    }

}
