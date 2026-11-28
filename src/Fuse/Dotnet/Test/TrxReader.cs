using System.Globalization;
using System.Xml.Linq;
using Fuse.Paths;
namespace Fuse.Dotnet.Test;

/// <summary>Reads the TRX files VSTest writes with <c>--logger trx</c>.</summary>
internal static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Reads every <c>.trx</c> file in <paramref name="directory"/>, or returns null when there is none.</summary>
    public static TestRunSummary? ReadDirectory(string directory, string root)
    {
        if (!Directory.Exists(directory))
            return null;
        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        if (files.Length == 0)
            return null;
        return files.Select(f => Read(f, root)).Aggregate(TestRunSummary.Empty, (a, b) => a.Add(b));
    }

    private static TestRunSummary Read(string file, string root)
    {
        var document = XDocument.Load(file);
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<TestFailure>();
        var records = new List<TestRecord>();
        foreach (var result in document.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName") ?? "?";
            var duration = Duration((string?)result.Attribute("duration"));
            switch ((string?)result.Attribute("outcome"))
            {
                case "Passed":
                    passed++;
                    records.Add(new TestRecord(name, TestVerdict.Passed, duration));
                    break;
                case "Failed" or "Error" or "Timeout" or "Aborted":
                    failed++;
                    var info = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
                    failures.Add(new TestFailure(
                        name,
                        Trim((string?)info?.Element(Ns + "Message") ?? "", 12),
                        Frames((string?)info?.Element(Ns + "StackTrace") ?? "", root)));
                    records.Add(new TestRecord(name, TestVerdict.Failed, duration));
                    break;
                default:
                    skipped++;
                    records.Add(new TestRecord(name, TestVerdict.Skipped, duration));
                    break;
            }
        }

        return new TestRunSummary(passed, failed, skipped, failures) { Records = records };
    }

    /// <summary>The <c>duration</c> attribute as a span; missing or unreadable reads as zero.</summary>
    private static TimeSpan Duration(string? value) =>
        value is not null && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;

    internal static string Trim(string message, int maxLines)
    {
        var lines = message.Replace("\r", "").Trim().Split('\n');
        return lines.Length <= maxLines ? string.Join('\n', lines) : string.Join('\n', lines.Take(maxLines)) + "\n...";
    }

    /// <summary>Stack frames that point into the repository, with paths made relative; framework frames are dropped.</summary>
    internal static List<string> Frames(string stack, string root)
    {
        var frames = new List<string>();
        foreach (var raw in stack.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(" in ", StringComparison.Ordinal);
            if (at < 0)
                continue;
            if (PathRules.UnderRoot(root, line[(at + 4)..]) is not { } relative)
                continue;
            frames.Add(line[..at] + " in " + relative);
            if (frames.Count == 5)
                break;
        }

        return frames;
    }
}
