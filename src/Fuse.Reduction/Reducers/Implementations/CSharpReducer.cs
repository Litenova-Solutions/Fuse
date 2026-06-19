using System.Text.RegularExpressions;

using Fuse.Reduction.Options;



namespace Fuse.Reduction.Reducers.Implementations;



/// <summary>

///     Reduces C# source files by removing comments, directives, and optional structural noise.

/// </summary>

public sealed class CSharpReducer : IContentReducer

{

    /// <inheritdoc />

    public string Extension => ".cs";



    /// <inheritdoc />

    public string Reduce(string content, ReductionOptions options)

    {

        content = RemoveComments(content, options);

        content = RemovePreprocessorDirectives(content, options);

        content = RemoveUsings(content, options);

        content = RemoveNamespaces(content, options);



        if (options.AggressiveCSharpReduction)

        {

            content = ApplyAggressiveOptimization(content);

            content = CompressSyntax(content);

        }



        return content.Trim();

    }



    private static string RemoveComments(string content, ReductionOptions options)

    {

        if (!options.RemoveCSharpComments)

            return content;



        const string pattern =

            @"(\$@""[^""]*(?:""""[^""]*)*"")|(@\$""[^""]*(?:""""[^""]*)*"")|(@""[^""]*(?:""""[^""]*)*"")|(\$""[^""\\]*(?:\\.[^""\\]*)*"")|(""[^""\\]*(?:\\.[^""\\]*)*"")|(//[^\r\n]*)|(/\*[\s\S]*?\*/)";



        return Regex.Replace(content, pattern, m =>

        {

            if (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success ||

                m.Groups[4].Success || m.Groups[5].Success)

                return m.Value;



            return string.Empty;

        });

    }



    private static string RemovePreprocessorDirectives(string content, ReductionOptions options)

    {

        if (options.RemoveCSharpRegions && !options.AggressiveCSharpReduction)

        {

            return Regex.Replace(content, @"^\s*#(region|endregion)[^\r\n]*", string.Empty,

                RegexOptions.Multiline);

        }



        if (options.AggressiveCSharpReduction)

            return Regex.Replace(content, @"^\s*#.*", string.Empty, RegexOptions.Multiline);



        return content;

    }



    private static string RemoveUsings(string content, ReductionOptions options)

    {

        if (!options.RemoveCSharpUsings)

            return content;



        content = Regex.Replace(content, @"^\s*using\s+[\w\.]+;\s*(\r?\n)?", string.Empty,

            RegexOptions.Multiline);

        content = Regex.Replace(content, @"^\s*using\s+[A-Za-z0-9_]+\s*=\s*[\w\.]+;\s*(\r?\n)?",

            string.Empty, RegexOptions.Multiline);



        return content;

    }



    private static string RemoveNamespaces(string content, ReductionOptions options)

    {

        if (!options.RemoveCSharpNamespaces)

            return content;



        content = Regex.Replace(content, @"^\s*namespace\s+[\w\.]+\s*;\s*(\r?\n)?", string.Empty,

            RegexOptions.Multiline);

        content = Regex.Replace(content, @"^\s*namespace\s+[\w\.]+\s*[\r\n\s]*\{", string.Empty,

            RegexOptions.Multiline);

        content = Regex.Replace(content, @"^(\s{4}|\t)", string.Empty, RegexOptions.Multiline);



        return content;

    }



    private static string ApplyAggressiveOptimization(string content)

    {

        var noiseAttributes = new[]

        {

            "DebuggerDisplay", "DebuggerStepThrough", "DebuggerNonUserCode",

            "MethodImpl", "EditorBrowsable", "Serializable", "Obsolete",

            "GeneratedCode", "CompilerGenerated", "ExcludeFromCodeCoverage",

            "SuppressMessage", "AssemblyVersion", "AssemblyFileVersion",

            "AssemblyTitle", "AssemblyDescription", "AssemblyConfiguration",

            "AssemblyCompany", "AssemblyProduct", "AssemblyCopyright",

            "AssemblyTrademark", "AssemblyCulture"

        };



        var attrPattern = $@"\[\s*({string.Join("|", noiseAttributes)})(\(.*\))?\s*\]\s*";

        content = Regex.Replace(content, attrPattern, string.Empty);

        content = Regex.Replace(content, @"^\s*\[assembly:\s*SuppressMessage.*\]\s*$", string.Empty,

            RegexOptions.Multiline);

        content = Regex.Replace(content, @"\bthis\.", string.Empty);

        content = Regex.Replace(content, @"\{\s*get;\s*set;\s*\}", "{get;set;}");

        content = Regex.Replace(content, @"\{\s*get;\s*\}", "{get;}");

        content = Regex.Replace(content, @"\{\s*set;\s*\}", "{set;}");



        return content;

    }



    private static string CompressSyntax(string content)

    {

        var literals = new List<string>();



        const string stringPattern =

            @"(\$@""[^""]*(?:""""[^""]*)*"")|(@\$""[^""]*(?:""""[^""]*)*"")|(@""[^""]*(?:""""[^""]*)*"")|(\$""[^""\\]*(?:\\.[^""\\]*)*"")|(""[^""\\]*(?:\\.[^""\\]*)*"")";



        content = Regex.Replace(content, stringPattern, m =>

        {

            literals.Add(m.Value);

            return $"__FUSE_STR_{literals.Count - 1}__";

        });



        content = Regex.Replace(content, @"\s*([{};,:()=\[\]])\s*", "$1");

        content = Regex.Replace(content, @"\s+", " ");



        content = Regex.Replace(content, @"__FUSE_STR_(\d+)__", m =>

        {

            if (int.TryParse(m.Groups[1].Value, out var index) && index >= 0 && index < literals.Count)

                return literals[index];



            return m.Value;

        });



        return content;

    }

}

