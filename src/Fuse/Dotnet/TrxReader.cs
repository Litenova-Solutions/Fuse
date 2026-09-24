using System.Xml.Linq;

namespace Fuse.Dotnet;

/// <summary>One failed test with its message and the stack frames inside the repository.</summary>
internal sealed record TestFailure(string Name, string Message, IReadOnlyList<string> Frames);

/// <summary>Counts and failures from one or more VSTest TRX files.</summary>
internal sealed record TestOutcome(int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures)
{
    public static TestOutcome Empty { get; } = new(0, 0, 0, []);

    public int Total => Passed + Failed + Skipped;

    public TestOutcome Add(TestOutcome other) =>
        new(Passed + other.Passed, Failed + other.Failed, Skipped + other.Skipped, [.. Failures, .. other.Failures]);
}

/// <summary>Reads the TRX files VSTest writes with <c>--logger trx</c>.</summary>
internal static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Reads every <c>.trx</c> file in <paramref name="directory"/>, or returns null when there is none.</summary>
    public static TestOutcome? ReadDirectory(string directory, string root)
    {
        if (!Directory.Exists(directory))
            return null;
        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        if (files.Length == 0)
            return null;
        return files.Select(f => Read(f, root)).Aggregate(TestOutcome.Empty, (a, b) => a.Add(b));
    }

    private static TestOutcome Read(string file, string root)
    {
        var document = XDocument.Load(file);
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<TestFailure>();
        foreach (var result in document.Descendants(Ns + "UnitTestResult"))
        {
            switch ((string?)result.Attribute("outcome"))
            {
                case "Passed":
                    passed++;
                    break;
                case "Failed" or "Error" or "Timeout" or "Aborted":
                    failed++;
                    var info = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
                    failures.Add(new TestFailure(
                        (string?)result.Attribute("testName") ?? "?",
                        Trim((string?)info?.Element(Ns + "Message") ?? "", 12),
                        Frames((string?)info?.Element(Ns + "StackTrace") ?? "", root)));
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new TestOutcome(passed, failed, skipped, failures);
    }

    private static string Trim(string message, int maxLines)
    {
        var lines = message.Replace("\r", "").Trim().Split('\n');
        return lines.Length <= maxLines ? string.Join('\n', lines) : string.Join('\n', lines.Take(maxLines)) + "\n...";
    }

    /// <summary>Stack frames that point into the repository, with paths made relative; framework frames are dropped.</summary>
    private static List<string> Frames(string stack, string root)
    {
        var frames = new List<string>();
        foreach (var raw in stack.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(" in ", StringComparison.Ordinal);
            if (at < 0)
                continue;
            var location = line[(at + 4)..];
            if (!location.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            frames.Add(line[..at] + " in " + Path.GetRelativePath(root, location).Replace('\\', '/'));
            if (frames.Count == 5)
                break;
        }

        return frames;
    }
}
