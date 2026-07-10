using System.Xml.Linq;

namespace Fuse.Workspace;

/// <summary>
///     One test's verdict parsed from a VSTest TRX result file (T1): the test's name and its outcome.
/// </summary>
/// <param name="Name">The test name as the runner reported it (usually the fully qualified method name).</param>
/// <param name="Outcome">The normalized outcome: <c>passed</c>, <c>failed</c>, or <c>not-run</c>.</param>
public sealed record TestVerdict(string Name, string Outcome);

/// <summary>
///     Parses the per-test verdicts from a VSTest TRX file (T1). The out-of-process test micro-host runs the
///     covering subset with <c>dotnet vstest ... --logger:trx</c> (which executes the emitted assembly directly,
///     with no MSBuild build) and this reads the resulting TRX into per-test outcomes the tool reports. Pure over
///     the TRX text, so it is unit-tested without running a real test host.
/// </summary>
public static class TrxResultParser
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>
    ///     Parses the unit-test verdicts from TRX content.
    /// </summary>
    /// <param name="trxXml">The TRX file content.</param>
    /// <returns>One verdict per recorded test result; empty when the TRX has none or does not parse.</returns>
    public static IReadOnlyList<TestVerdict> Parse(string trxXml)
    {
        if (string.IsNullOrWhiteSpace(trxXml))
            return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(trxXml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var verdicts = new List<TestVerdict>();
        foreach (var result in document.Descendants(Trx + "UnitTestResult"))
        {
            var name = result.Attribute("testName")?.Value;
            if (string.IsNullOrEmpty(name))
                continue;
            verdicts.Add(new TestVerdict(name, Normalize(result.Attribute("outcome")?.Value)));
        }

        return verdicts;
    }

    // Maps the TRX outcome vocabulary to the three states the tool reports. Anything not passed or failed (for
    // example NotExecuted, Inconclusive, Timeout, Aborted) is not-run, so a test the host could not execute is
    // never reported as passed.
    private static string Normalize(string? outcome) => outcome switch
    {
        "Passed" => "passed",
        "Failed" => "failed",
        _ => "not-run",
    };
}
