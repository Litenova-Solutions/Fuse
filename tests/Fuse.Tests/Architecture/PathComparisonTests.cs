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
            .SelectMany(f => f.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>()
                .Where(c => AsksForTheOperatingSystem(c.Condition) && (NamesComparison(c.WhenTrue) || NamesComparison(c.WhenFalse)))
                .Select(c => $"{f.Path}:{c.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {c}; compare RepoPath values instead"))
            .ToList();

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [Fact]
    public void Only_Fuse_Paths_uses_the_file_system_comparison_by_name()
    {
        var found = Files.Value
            .Where(f => !f.Path.StartsWith(Paths, StringComparison.Ordinal))
            .SelectMany(f => f.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Name.Identifier.ValueText == "Comparison" && LastName(m.Expression) == "RepoPath")
                .Select(m => $"{f.Path}:{m.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {m}; compare RepoPath values instead"))
            .ToList();

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

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

    /// <summary>True when the condition calls one of <c>OperatingSystem</c>'s platform checks, negated or not.</summary>
    private static bool AsksForTheOperatingSystem(ExpressionSyntax condition) =>
        condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax access
            && LastName(access.Expression) == "OperatingSystem"
            && access.Name.Identifier.ValueText.StartsWith("Is", StringComparison.Ordinal));

    /// <summary>True for a member of <c>StringComparer</c> or <c>StringComparison</c>, such as <c>StringComparer.Ordinal</c>.</summary>
    private static bool NamesComparison(ExpressionSyntax branch) =>
        branch is MemberAccessExpressionSyntax access && LastName(access.Expression) is "StringComparer" or "StringComparison";

    /// <summary>The rightmost simple name of a possibly qualified name, so <c>System.StringComparer</c> counts as <c>StringComparer</c>.</summary>
    private static string? LastName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
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
