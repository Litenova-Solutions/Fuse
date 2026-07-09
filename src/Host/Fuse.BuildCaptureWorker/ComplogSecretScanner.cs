using Basic.CompilerLog.Util;
using Fuse.Reduction.Security;

namespace Fuse.BuildCaptureWorker;

/// <summary>
///     A secret found in a compiler-log artifact that would ship in a capture bundle (C2): the match class and the
///     labelled artifact it was found in. The secret value itself is never carried, so a fail-closed report names
///     what class of secret was matched and where, never the secret.
/// </summary>
/// <param name="Kind">The secret class (for example <c>connection-string</c>, <c>high-entropy</c>), never the value.</param>
/// <param name="Label">The artifact the match was in (for example <c>generated:Foo.g.cs</c> or <c>additionalfile:appsettings.json</c>).</param>
public sealed record ComplogSecretFinding(string Kind, string Label);

/// <summary>
///     Fail-closed secret scanner for the portable compiler log that a capture bundle ships (C2). It scans the
///     build-injected artifacts a bundle newly exposes - the generated documents (source generators can embed a
///     value read at build time) and the additional files the compilation carried (configuration such as
///     appsettings that a build includes) - with the same <see cref="ISecretRedactor" /> the reduction pipeline
///     uses. The repository's own source is not scanned: it is the repository's pre-existing exposure (already in
///     version control), not a new bundle exposure, and scanning it would false-positive on a repository's own
///     secret-handling test data.
/// </summary>
/// <remarks>
///     The scan is fail-closed by contract: any finding, or any error that prevents a complete scan, must be
///     treated by the caller as "do not ship the bundle". The scanner returns the first finding (a single match is
///     enough to fail the capture), naming its class and artifact but never the value.
/// </remarks>
public static class ComplogSecretScanner
{
    /// <summary>
    ///     Scans a sequence of labelled texts for secrets, returning the first finding or null when all are clean.
    ///     Pure and deterministic - the unit-testable core, independent of any compiler log.
    /// </summary>
    /// <param name="texts">The labelled texts to scan (label plus content).</param>
    /// <param name="redactor">The secret detector.</param>
    /// <returns>The first secret finding (class plus label), or null when no text contains a secret.</returns>
    public static ComplogSecretFinding? FindFirstSecret(
        IEnumerable<(string Label, string Text)> texts, ISecretRedactor redactor)
    {
        foreach (var (label, text) in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var spans = redactor.FindSecretSpans(text);
            if (spans.Count > 0)
                return new ComplogSecretFinding(spans[0].Kind, label);
        }

        return null;
    }

    /// <summary>
    ///     Scans the generated documents and additional files recorded in a compiler log for secrets. Reads the
    ///     compiler log (a binlog or complog) and gathers, across each C# compiler call, the generated syntax
    ///     trees and additional-text contents, then delegates to <see cref="FindFirstSecret" />.
    /// </summary>
    /// <param name="compilerLogPath">The path to the compiler log (a <c>.complog</c> or <c>.binlog</c>).</param>
    /// <param name="redactor">The secret detector; defaults to the shipped <see cref="DefaultSecretRedactor" />.</param>
    /// <param name="cancellationToken">A token to cancel the scan.</param>
    /// <returns>The first secret finding, or null when the scanned artifacts are clean.</returns>
    public static ComplogSecretFinding? ScanCompilerLog(
        string compilerLogPath, ISecretRedactor? redactor, CancellationToken cancellationToken)
    {
        redactor ??= new DefaultSecretRedactor();
        using var reader = CompilerCallReaderUtil.Create(compilerLogPath);
        return FindFirstSecret(EnumerateBundleTexts(reader, cancellationToken), redactor);
    }

    // The build-injected texts a bundle exposes, across every C# compiler call: each generated syntax tree's
    // source and each additional file's content, labelled by their origin.
    private static IEnumerable<(string Label, string Text)> EnumerateBundleTexts(
        ICompilerCallReader reader, CancellationToken cancellationToken)
    {
        foreach (var data in reader.ReadAllCompilationData())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.CompilerCall.IsCSharp != true)
                continue;

            foreach (var tree in data.GetGeneratedSyntaxTrees(cancellationToken))
                yield return ($"generated:{Path.GetFileName(tree.FilePath)}", tree.ToString());

            foreach (var additional in data.AdditionalTexts)
            {
                var text = additional.GetText(cancellationToken)?.ToString();
                if (text is not null)
                    yield return ($"additionalfile:{Path.GetFileName(additional.Path)}", text);
            }
        }
    }
}
