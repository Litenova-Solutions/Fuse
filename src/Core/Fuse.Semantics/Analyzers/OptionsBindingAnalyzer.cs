using System.Text.Json;
using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers options/configuration wiring: which configuration section binds to which options type, and
///     which types consume those options.
/// </summary>
/// <remarks>
///     Detects <c>services.Configure&lt;TOptions&gt;(configuration.GetSection("Section"))</c> and emits
///     <c>config:Section -&gt; TOptions : options_binds</c> (weight 0.85) plus an
///     <see cref="OptionsBindingRecord" />. Detects constructor parameters of
///     <c>IOptions&lt;T&gt;</c>/<c>IOptionsMonitor&lt;T&gt;</c>/<c>IOptionsSnapshot&lt;T&gt;</c> and emits
///     <c>consumer -&gt; T : options_consumes</c> (weight 0.75). Also indexes top-level sections of
///     <c>appsettings*.json</c> as config nodes so a configuration section resolves even without a bind call.
/// </remarks>
public sealed class OptionsBindingAnalyzer : ISemanticAnalyzer
{
    private const double BindsWeight = 0.85;
    private const double ConsumesWeight = 0.75;

    private static readonly HashSet<string> OptionsInterfaces =
        new(StringComparer.Ordinal) { "IOptions", "IOptionsMonitor", "IOptionsSnapshot" };

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var bindings = new List<OptionsBindingRecord>();

        IndexConfigSections(root, nodes, cancellationToken);
        CollectBindings(compilation, root, nodes, edges, bindings, cancellationToken);
        CollectConsumers(compilation, root, nodes, edges, cancellationToken);

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, [], [], bindings, []);
    }

    private static void CollectBindings(
        Compilation compilation,
        string root,
        Dictionary<string, NodeRecord> nodes,
        List<SemanticEdgeRecord> edges,
        List<OptionsBindingRecord> bindings,
        CancellationToken cancellationToken)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var filePath = NormalizeRelative(root, tree.FilePath);

            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (GetGenericName(invocation) is not { Identifier.ValueText: "Configure" } generic
                    || generic.TypeArgumentList.Arguments.Count != 1)
                {
                    continue;
                }

                if (model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Type is not INamedTypeSymbol optionsType)
                    continue;

                var section = FindSectionName(invocation);
                if (section is null)
                    continue;

                var span = invocation.GetLocation().GetLineSpan();
                var startLine = span.StartLinePosition.Line + 1;
                var endLine = span.EndLinePosition.Line + 1;
                var configId = SemanticNodes.ConfigId(section);

                nodes[configId] = ConfigNode(configId, section, filePath);
                var optionsNode = SemanticNodes.TypeNode(optionsType, root);
                nodes[optionsNode.NodeId] = optionsNode;

                edges.Add(new SemanticEdgeRecord(
                    FromNodeId: configId,
                    ToNodeId: optionsNode.NodeId,
                    EdgeType: "options_binds",
                    Weight: BindsWeight,
                    Confidence: 0.9,
                    Evidence: $"Configure<{optionsType.Name}>(GetSection(\"{section}\"))",
                    EvidenceFilePath: filePath,
                    EvidenceStartLine: startLine,
                    EvidenceEndLine: endLine));

                bindings.Add(new OptionsBindingRecord(
                    BindingId: $"options:{filePath}:{startLine}:{optionsType.Name}",
                    OptionsName: optionsType.ToDisplayString(SemanticNodes.NodeNameFormat),
                    FilePath: filePath,
                    StartLine: startLine,
                    EndLine: endLine,
                    BindingKind: "configure",
                    Confidence: 0.9,
                    OptionsSymbolId: SymbolIdBuilder.Build(optionsType),
                    ConfigSection: section,
                    Evidence: invocation.ToString()));
            }
        }
    }

    private static void CollectConsumers(
        Compilation compilation,
        string root,
        Dictionary<string, NodeRecord> nodes,
        List<SemanticEdgeRecord> edges,
        CancellationToken cancellationToken)
    {
        foreach (var type in SemanticNodes.EnumerateTypes(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.TypeKind != TypeKind.Class || !SemanticNodes.IsInSource(type, compilation))
                continue;

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.IsImplicitlyDeclared)
                    continue;

                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } wrapper
                        || !OptionsInterfaces.Contains(wrapper.Name)
                        || wrapper.TypeArguments[0] is not INamedTypeSymbol optionsType)
                    {
                        continue;
                    }

                    var consumerNode = SemanticNodes.TypeNode(type, root);
                    var optionsNode = SemanticNodes.TypeNode(optionsType, root);
                    nodes[consumerNode.NodeId] = consumerNode;
                    nodes[optionsNode.NodeId] = optionsNode;
                    edges.Add(new SemanticEdgeRecord(
                        FromNodeId: consumerNode.NodeId,
                        ToNodeId: optionsNode.NodeId,
                        EdgeType: "options_consumes",
                        Weight: ConsumesWeight,
                        Confidence: 0.9,
                        Evidence: $"{type.Name}({wrapper.Name}<{optionsType.Name}>)",
                        EvidenceFilePath: consumerNode.FilePath));
                }
            }
        }
    }

    // Index top-level sections of appsettings*.json as config nodes so a section resolves even when no bind
    // call names it. Malformed or unreadable files are skipped.
    private static void IndexConfigSections(string root, Dictionary<string, NodeRecord> nodes, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                var filePath = NormalizeRelative(root, file);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var configId = SemanticNodes.ConfigId(property.Name);
                    nodes.TryAdd(configId, ConfigNode(configId, property.Name, filePath));
                }
            }
            catch (JsonException)
            {
                // Skip malformed configuration files.
            }
            catch (IOException)
            {
                // Skip unreadable configuration files.
            }
        }
    }

    private static NodeRecord ConfigNode(string nodeId, string section, string? filePath) =>
        new(
            NodeId: nodeId,
            Kind: "config",
            DisplayName: section,
            StableKey: nodeId,
            FilePath: filePath);

    private static GenericNameSyntax? GetGenericName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
        GenericNameSyntax generic => generic,
        _ => null,
    };

    private static string? FindSectionName(InvocationExpressionSyntax configure)
    {
        // Look for a GetSection("X") call anywhere in the Configure arguments.
        foreach (var inner in configure.ArgumentList.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inner.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetSection" }
                && inner.ArgumentList.Arguments.Count > 0
                && inner.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }
        }

        return null;
    }

    private static string NormalizeRelative(string rootDirectory, string absolutePath) =>
        string.IsNullOrEmpty(absolutePath)
            ? absolutePath
            : Path.GetRelativePath(rootDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
