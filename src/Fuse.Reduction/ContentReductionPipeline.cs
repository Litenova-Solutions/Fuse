using System.Text.RegularExpressions;

using Fuse.Collection.FileSystem;

using Fuse.Collection.Models;

using Fuse.Reduction.Models;

using Fuse.Reduction.Options;

using Fuse.Reduction.Reducers;

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

    private readonly ITokenCounter _tokenCounter;



    /// <summary>

    ///     Initializes a new instance of the <see cref="ContentReductionPipeline" /> class.

    /// </summary>

    /// <param name="fileSystem">The file system used to read source file content.</param>

    /// <param name="reducerRegistry">The registry that resolves reducers by extension.</param>

    /// <param name="tokenCounter">The token counter used when constructing fused content.</param>

    public ContentReductionPipeline(

        IFileSystem fileSystem,

        ReducerRegistry reducerRegistry,

        ITokenCounter tokenCounter)

    {

        _fileSystem = fileSystem;

        _reducerRegistry = reducerRegistry;

        _tokenCounter = tokenCounter;

    }



    /// <summary>

    ///     Reduces the supplied source files and returns non-trivial fused content.

    /// </summary>

    /// <param name="sourceFiles">The files to read and reduce.</param>

    /// <param name="options">The reduction options for the current run.</param>

    /// <param name="cancellationToken">A token to cancel the operation.</param>

    /// <returns>A list of non-trivial fused content items.</returns>

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



            var fused = new FusedContent(sourceFile, content, _tokenCounter);



            if (!fused.IsTrivial)

                results.Add(fused);

        }



        return results;

    }



    private static string NormalizeWhitespace(string content, ReductionOptions options)

    {

        if (options.TrimContent)

        {

            content = Regex.Replace(content, @"^[\s\t]+|[\s\t]+$", string.Empty,

                RegexOptions.Multiline);

        }



        if (options.UseCondensing)

        {

            content = Regex.Replace(content, @"^\s*$\r?\n", string.Empty,

                RegexOptions.Multiline);

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

