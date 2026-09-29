using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Tests.Architecture;

/// <summary>
///     Paths are compared in one place. <c>RepoPath</c> compares the way the file system does, so a string comparison
///     chosen by the operating system anywhere else is a path comparison that goes around it, and a wire record that held
///     a <c>RepoPath</c> would change what the pipe carries, which is strings.
/// </summary>
/// <remarks>
///     Every rule is read from the syntax of the files under <c>src/Fuse</c>, so a name in a comment or a string is not a
///     use. A comparer for paths that is not chosen by the operating system (a bare <c>StringComparer.OrdinalIgnoreCase</c>)
///     cannot be told from one for names without types, so review catches that one.
/// </remarks>
public class PathComparisonTests
{
    private const string Paths = "src/Fuse/Paths/";
    private const string RepoPath = "src/Fuse/Paths/RepoPath.cs";
    private const string Protocol = "src/Fuse/Protocol/";

    private static readonly Lazy<IReadOnlyList<(string Path, CompilationUnitSyntax Root)>> Files = new(Read);

    [Fact]
    public void Only_RepoPath_chooses_a_string_comparison_by_the_operating_system()
    {
        // A comparer or comparison picked with OperatingSystem.IsWindows() is the file system's comparison written a
        // second time, whether as a PathRules member or inline beside a path.
        var found = Files.Value
            .Where(f => f.Path != RepoPath)
            .SelectMany(f => ComparisonsChosenByPlatform(f.Root).Select(n => $"{f.Path}:{n.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {n}; compare RepoPath values instead"))
            .ToList();

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [Fact]
    public void Only_Fuse_Paths_uses_the_file_system_comparison_by_name()
    {
        var found = Files.Value
            .Where(f => !f.Path.StartsWith(Paths, StringComparison.Ordinal))
            .SelectMany(f => UsesOfTheComparison(f.Root).Select(n => $"{f.Path}:{n.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {n}; compare RepoPath values instead"))
            .ToList();

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    /// <summary>The ways of choosing a comparison by platform the first rule catches, each written as code would write it.</summary>
    [Theory]
    [InlineData("var c = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;")]
    [InlineData("var c = !System.OperatingSystem.IsLinux() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;")]
    [InlineData("StringComparison c; if (OperatingSystem.IsWindows()) c = StringComparison.OrdinalIgnoreCase; else c = StringComparison.Ordinal;")]
    [InlineData("var c = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;")]
    [InlineData("var windows = OperatingSystem.IsWindows(); var c = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;")]
    [InlineData("var c = IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;", "using static System.OperatingSystem;")]
    public void A_comparison_chosen_by_platform_is_found(string statements, string usings = "")
    {
        Assert.NotEmpty(ComparisonsChosenByPlatform(Parse(statements, usings)));
    }

    /// <summary>Code the first rule leaves alone: a platform check that chooses no comparison, and a comparison that does not depend on one.</summary>
    [Theory]
    [InlineData("var name = OperatingSystem.IsWindows() ? \"fuse.exe\" : \"fuse\";")]
    [InlineData("if (OperatingSystem.IsWindows()) Console.WriteLine(1); var c = StringComparison.Ordinal;")]
    [InlineData("var c = verbose ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;")]
    public void Code_that_does_not_choose_a_comparison_by_platform_is_not_found(string statements)
    {
        Assert.Empty(ComparisonsChosenByPlatform(Parse(statements, "")));
    }

    [Fact]
    public void The_comparison_named_through_using_static_is_found()
    {
        Assert.NotEmpty(UsesOfTheComparison(Parse("var c = Comparison;", "using static Fuse.Paths.RepoPath;")));
        Assert.NotEmpty(UsesOfTheComparison(Parse("var c = Fuse.Paths.RepoPath.Comparison;", "")));
        Assert.Empty(UsesOfTheComparison(Parse("var c = Comparison;", "")));
    }

    /// <summary>
    ///     Conditional expressions and <c>if</c> statements whose condition asks for the platform and whose branches name a
    ///     member of <c>StringComparer</c> or <c>StringComparison</c>.
    /// </summary>
    private static IEnumerable<SyntaxNode> ComparisonsChosenByPlatform(CompilationUnitSyntax root)
    {
        var platformFlags = PlatformFlags(root);
        foreach (var conditional in root.DescendantNodes().OfType<ConditionalExpressionSyntax>())
        {
            if (AsksForTheOperatingSystem(conditional.Condition, root, platformFlags) && (NamesComparison(conditional.WhenTrue) || NamesComparison(conditional.WhenFalse)))
                yield return conditional;
        }

        foreach (var statement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (AsksForTheOperatingSystem(statement.Condition, root, platformFlags)
                && new SyntaxNode?[] { statement.Statement, statement.Else }.OfType<SyntaxNode>().SelectMany(b => b.DescendantNodesAndSelf()).OfType<MemberAccessExpressionSyntax>().Any(NamesComparison))
                yield return statement;
        }
    }

    /// <summary><c>RepoPath.Comparison</c> by its qualified name, or by its simple name in a file that imports <c>RepoPath</c>'s members.</summary>
    private static IEnumerable<SyntaxNode> UsesOfTheComparison(CompilationUnitSyntax root)
    {
        var imported = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.StaticKeyword != default && u.Name is { } name && LastName(name) == "RepoPath");
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Name.Identifier.ValueText == "Comparison" && LastName(access.Expression) == "RepoPath")
                yield return access;
        }

        if (!imported)
            yield break;
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            // A qualified use is found above, and a member of something else that is also called Comparison is not this one.
            var isMemberName = identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier;
            if (identifier.Identifier.ValueText == "Comparison" && !isMemberName)
                yield return identifier;
        }
    }

    /// <summary>The names of fields, properties and locals in the file whose value is a platform check, such as <c>var windows = OperatingSystem.IsWindows();</c>.</summary>
    private static HashSet<string> PlatformFlags(CompilationUnitSyntax root)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer is { Value: var value } && AsksForTheOperatingSystem(value, root, []))
                flags.Add(variable.Identifier.ValueText);
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if ((property.ExpressionBody?.Expression ?? property.Initializer?.Value) is { } value && AsksForTheOperatingSystem(value, root, []))
                flags.Add(property.Identifier.ValueText);
        }

        return flags;
    }

    private static CompilationUnitSyntax Parse(string statements, string usings) =>
        CSharpSyntaxTree.ParseText($"{usings}\nclass C {{ void M(bool verbose) {{ {statements} }} }}").GetCompilationUnitRoot();

    [Fact]
    public void The_wire_carries_paths_as_strings()
    {
        var protocol = Files.Value.Where(f => f.Path.StartsWith(Protocol, StringComparison.Ordinal)).ToList();
        var found = protocol
            .SelectMany(f => f.Root.DescendantNodes().OfType<SimpleNameSyntax>()
                .Where(n => n.Identifier.ValueText == "RepoPath")
                .Select(n => $"{f.Path}:{n.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: the wire names RepoPath; carry the path as a string and convert it in the engine or the client"))
            .ToList();

        Assert.NotEmpty(protocol);
        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    /// <summary>
    ///     True when the expression asks for the platform, negated or not: it calls one of <c>OperatingSystem</c>'s checks
    ///     (by its qualified name, or by its simple name in a file that imports its members) or
    ///     <c>RuntimeInformation.IsOSPlatform</c>, or it names one of <paramref name="platformFlags"/>.
    /// </summary>
    private static bool AsksForTheOperatingSystem(ExpressionSyntax condition, CompilationUnitSyntax root, HashSet<string> platformFlags)
    {
        var importsOperatingSystem = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.StaticKeyword != default && u.Name is { } name && LastName(name) == "OperatingSystem");
        return condition.DescendantNodesAndSelf().Any(node => node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } =>
                (LastName(access.Expression) == "OperatingSystem" && access.Name.Identifier.ValueText.StartsWith("Is", StringComparison.Ordinal))
                || (LastName(access.Expression) == "RuntimeInformation" && access.Name.Identifier.ValueText == "IsOSPlatform"),
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax name } => importsOperatingSystem && name.Identifier.ValueText.StartsWith("Is", StringComparison.Ordinal),
            IdentifierNameSyntax flag => flag.Parent is not InvocationExpressionSyntax && platformFlags.Contains(flag.Identifier.ValueText),
            _ => false,
        });
    }

    /// <summary>True for a member of <c>StringComparer</c> or <c>StringComparison</c>, such as <c>StringComparer.Ordinal</c>.</summary>
    private static bool NamesComparison(ExpressionSyntax branch) =>
        branch is MemberAccessExpressionSyntax access && LastName(access.Expression) is "StringComparer" or "StringComparison";

    /// <summary>The rightmost simple name of a possibly qualified name, so <c>System.StringComparer</c> counts as <c>StringComparer</c>.</summary>
    private static string? LastName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => null,
    };

    private static List<(string Path, CompilationUnitSyntax Root)> Read()
    {
        var repository = RepositoryRoot();
        var source = Path.Combine(repository, "src", "Fuse");
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        return Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(source, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(repository, f).Replace('\\', '/'), CSharpSyntaxTree.ParseText(File.ReadAllText(f), options).GetCompilationUnitRoot()))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fuse.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException($"no Fuse.slnx above {AppContext.BaseDirectory}");
    }
}
