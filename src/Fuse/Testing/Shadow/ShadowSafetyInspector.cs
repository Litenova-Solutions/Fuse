using System.Collections.Concurrent;
using Fuse.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Testing.Shadow;

/// <summary>
///     Whether a test project walks parent directories from its own build output, which a shadow run moves.
///     A test helper that reads <c>AppContext.BaseDirectory</c>, <c>AppDomain.CurrentDomain.BaseDirectory</c>,
///     <c>Assembly.Location</c> or <c>Assembly.CodeBase</c> and also walks up from it finds the repository from
///     build output, so from the shadow copy it finds the shadow folder instead and the tests fail.
/// </summary>
/// <remarks>
///     The answer covers the test project and the projects it references that are in the solution, read from the
///     generation's current solution. A document counts when one method or one class both reads a base directory
///     and walks up from it (a <c>Parent</c> read, <c>Directory.GetParent</c>, or a <c>..</c> path segment). A
///     <c>CallerFilePath</c> use and a direct read of a file inside the base directory without walking up do not
///     count. The answer per document is kept by its text version, so a later plan re-reads only changed files.
/// </remarks>
internal sealed class ShadowSafetyInspector
{
    private readonly ConcurrentDictionary<DocumentId, (VersionStamp Version, bool Unsafe)> _answers = new();

    /// <summary>
    ///     Why the test project of <paramref name="node"/> cannot run from a shadow copy, or null when it can.
    ///     The reason names the file that both reads a base directory and walks up from it.
    /// </summary>
    /// <param name="node">The test project the plan lists.</param>
    /// <param name="variants">The Roslyn projects per target framework from the pinned generation.</param>
    /// <param name="snapshot">The generation's current solution, which holds the syntax trees.</param>
    /// <param name="cancellationToken">Cancels reading syntax trees.</param>
    internal async Task<string?> SkipReasonAsync(ProjectNode node, IReadOnlyList<Project> variants, Solution snapshot, CancellationToken cancellationToken)
    {
        var projects = Closure(variants, snapshot);
        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (await IsUnsafeAsync(document, cancellationToken).ConfigureAwait(false))
                    return $"walks parent directories from AppContext.BaseDirectory or Assembly.Location in {FileNameOf(document)}";
            }
        }

        return null;
    }

    /// <summary>The test projects' Roslyn projects and every project they reference through the snapshot, once each.</summary>
    private static List<Project> Closure(IReadOnlyList<Project> variants, Solution snapshot)
    {
        var seen = new HashSet<ProjectId>();
        var projects = new List<Project>();
        var stack = new Stack<Project>(variants);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current.Id))
                continue;
            projects.Add(current);
            foreach (var reference in current.ProjectReferences)
            {
                if (snapshot.GetProject(reference.ProjectId) is { } referenced)
                    stack.Push(referenced);
            }
        }

        return projects;
    }

    /// <summary>Whether <paramref name="document"/> both reads a base directory and walks up from it in one method or class.</summary>
    private async Task<bool> IsUnsafeAsync(Document document, CancellationToken cancellationToken)
    {
        try
        {
            var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            if (_answers.TryGetValue(document.Id, out var cached) && cached.Version == version)
                return cached.Unsafe;

            var unsafeDocument = false;
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root)
                unsafeDocument = ScopesOf(root).Any(scope => ReadsBase(scope) && WalksUp(scope));

            _answers[document.Id] = (version, unsafeDocument);
            return unsafeDocument;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Each method or class that can hold both halves: every type, every method-like member, and the whole file when it holds neither.</summary>
    private static List<SyntaxNode> ScopesOf(SyntaxNode root)
    {
        var scopes = new List<SyntaxNode>();
        scopes.AddRange(root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>());
        scopes.AddRange(root.DescendantNodes().OfType<MethodDeclarationSyntax>());
        scopes.AddRange(root.DescendantNodes().OfType<ConstructorDeclarationSyntax>());
        scopes.AddRange(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
        scopes.AddRange(root.DescendantNodes().OfType<AccessorDeclarationSyntax>());
        if (scopes.Count == 0)
            scopes.Add(root);
        return scopes;
    }

    /// <summary>Whether <paramref name="scope"/> reads a base directory a shadow copy moves.</summary>
    private static bool ReadsBase(SyntaxNode scope) =>
        scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(IsBaseRead);

    /// <summary>Whether <paramref name="scope"/> walks up from a directory: a <c>Parent</c> read, <c>GetParent</c>, or a <c>..</c> segment.</summary>
    private static bool WalksUp(SyntaxNode scope)
    {
        foreach (var access in scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            var name = access.Name.Identifier.ValueText;
            if (name == "Parent" || name == "GetParent")
                return true;
        }

        foreach (var literal in scope.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
        {
            if (HasDotDot(literal.Token.ValueText))
                return true;
        }

        foreach (var text in scope.DescendantNodesAndSelf().OfType<InterpolatedStringTextSyntax>())
        {
            if (HasDotDot(text.TextToken.ValueText))
                return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="access"/> reads a base directory: <c>BaseDirectory</c> off the application context or domain, or <c>Location</c> or <c>CodeBase</c> off an assembly.</summary>
    private static bool IsBaseRead(MemberAccessExpressionSyntax access)
    {
        var name = access.Name.Identifier.ValueText;
        if (name == "BaseDirectory")
        {
            var qualifier = access.Expression.ToString();
            return qualifier.Contains("AppContext", StringComparison.Ordinal)
                || qualifier.Contains("AppDomain", StringComparison.Ordinal)
                || qualifier.Contains("CurrentDomain", StringComparison.Ordinal);
        }

        if (name == "Location" || name == "CodeBase")
            return access.Expression.ToString().Contains("Assembly", StringComparison.Ordinal);

        return false;
    }

    /// <summary>Whether <paramref name="value"/> holds a <c>..</c> path segment.</summary>
    private static bool HasDotDot(string value) =>
        value.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);

    /// <summary>The file name of <paramref name="document"/>, for the skip reason.</summary>
    private static string FileNameOf(Document document)
    {
        if (document.FilePath is { } path && Path.GetFileName(path) is { Length: > 0 } file)
            return file;
        return document.Name;
    }
}
