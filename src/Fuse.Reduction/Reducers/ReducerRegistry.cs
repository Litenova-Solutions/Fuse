namespace Fuse.Reduction.Reducers;



/// <summary>

///     Resolves content reducers by file extension.

/// </summary>

public sealed class ReducerRegistry

{

    private static readonly Dictionary<string, string> ExtensionAliases =

        new(StringComparer.OrdinalIgnoreCase)

        {

            [".htm"] = ".html",

            [".yml"] = ".yaml",

            [".targets"] = ".xml",

            [".props"] = ".xml",

            [".csproj"] = ".xml",

            [".cshtml"] = ".razor",

        };



    private readonly Dictionary<string, IContentReducer> _reducers;



    /// <summary>

    ///     Initializes a new instance of the <see cref="ReducerRegistry" /> class.

    /// </summary>

    /// <param name="reducers">The registered content reducers.</param>

    public ReducerRegistry(IEnumerable<IContentReducer> reducers)

    {

        _reducers = reducers.ToDictionary(

            reducer => reducer.Extension,

            StringComparer.OrdinalIgnoreCase);

    }



    /// <summary>

    ///     Attempts to resolve a reducer for the specified file extension.

    /// </summary>

    /// <param name="extension">The file extension, including the leading dot.</param>

    /// <returns>The matching reducer, or <c>null</c> when none is registered.</returns>

    public IContentReducer? TryGetReducer(string extension)

    {

        if (_reducers.TryGetValue(extension, out var reducer))

            return reducer;



        if (ExtensionAliases.TryGetValue(extension, out var alias) &&

            _reducers.TryGetValue(alias, out reducer))

            return reducer;



        return null;

    }

}

