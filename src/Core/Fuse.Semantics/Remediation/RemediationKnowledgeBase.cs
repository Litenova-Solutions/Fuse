using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     One entry in the environment-remediation knowledge base (C1): a failure signature matched against the
///     build or restore output (and <c>fuse doctor</c>'s per-project reason strings), paired with the remedy that
///     addresses it. The knowledge base ships as data so the troubleshooting docs can be generated from it and a
///     test can diff its keys against the page.
/// </summary>
/// <param name="Id">The stable signature id, typically the diagnostic code (for example <c>NU1507</c>).</param>
/// <param name="Pattern">A regular expression matched against the diagnostic output to detect the failure.</param>
/// <param name="Title">A one-line human-readable description of the failure.</param>
/// <param name="Remedy">
///     The remedy key the <c>fuse up</c> engine dispatches on (for example <c>overlay-nuget-source-mapping</c>,
///     <c>install-sdk</c>, <c>install-workload</c>, or <c>classify-only</c> when the failure is repository code
///     Fuse must not touch).
/// </param>
/// <param name="RequiresConsent">Whether applying the remedy changes the machine and so needs an explicit consent flag.</param>
/// <param name="Explanation">Why the failure happens and what the remedy does.</param>
public sealed record RemediationSignature(
    string Id,
    string Pattern,
    string Title,
    string Remedy,
    bool RequiresConsent,
    string Explanation);

/// <summary>
///     Source-generated JSON context for the remediation knowledge base, per the project invariant that JSON uses
///     a source-generated <see cref="JsonSerializerContext" /> rather than reflection serialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemediationSignature[]))]
public sealed partial class RemediationKnowledgeBaseJsonContext : JsonSerializerContext;

/// <summary>
///     The environment-remediation knowledge base (C1): matches a build or restore failure to the remedy that
///     addresses it. This is the data-driven core the <c>fuse up</c> engine dispatches on; matching is
///     classify-only here (no remedy is applied), so this type is safe to use before the remediation actions
///     exist.
/// </summary>
/// <remarks>
///     The default knowledge base is loaded from an embedded JSON resource. Each signature's regular expression is
///     compiled once at construction; <see cref="Match" /> returns the first signature whose pattern is found in
///     the supplied output, in declaration order, so more specific signatures should precede broader ones.
/// </remarks>
public sealed class RemediationKnowledgeBase
{
    private readonly IReadOnlyList<(RemediationSignature Signature, Regex Matcher)> _signatures;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RemediationKnowledgeBase" /> class from a set of signatures.
    /// </summary>
    /// <param name="signatures">The signatures, in match-precedence (declaration) order.</param>
    public RemediationKnowledgeBase(IReadOnlyList<RemediationSignature> signatures) =>
        _signatures = signatures
            .Select(s => (s, new Regex(s.Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled)))
            .ToList();

    /// <summary>The signatures in the knowledge base, in match-precedence order.</summary>
    public IReadOnlyList<RemediationSignature> Signatures => _signatures.Select(s => s.Signature).ToList();

    /// <summary>
    ///     Loads the default knowledge base from the embedded JSON resource.
    /// </summary>
    /// <returns>The default knowledge base.</returns>
    /// <exception cref="InvalidOperationException">The embedded resource is missing or does not deserialize.</exception>
    public static RemediationKnowledgeBase LoadDefault()
    {
        var assembly = typeof(RemediationKnowledgeBase).Assembly;
        var resourceName = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith("remediation-kb.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The remediation knowledge base resource was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The remediation knowledge base resource could not be opened.");
        var signatures = JsonSerializer.Deserialize(stream, RemediationKnowledgeBaseJsonContext.Default.RemediationSignatureArray)
            ?? throw new InvalidOperationException("The remediation knowledge base did not deserialize.");
        return new RemediationKnowledgeBase(signatures);
    }

    /// <summary>
    ///     Finds the remedy signature for a build or restore output, if any.
    /// </summary>
    /// <param name="diagnosticOutput">The build/restore output or a <c>fuse doctor</c> reason string.</param>
    /// <returns>The first matching signature in precedence order, or null when nothing matches.</returns>
    public RemediationSignature? Match(string? diagnosticOutput)
    {
        if (string.IsNullOrEmpty(diagnosticOutput))
            return null;
        foreach (var (signature, matcher) in _signatures)
        {
            if (matcher.IsMatch(diagnosticOutput))
                return signature;
        }

        return null;
    }
}
