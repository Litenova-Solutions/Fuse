namespace Fuse.Dotnet;

/// <summary>One test as it ended in a run: its name, its verdict, and how long it took.</summary>
/// <param name="Name">The fully qualified test name.</param>
/// <param name="Verdict">How the test ended.</param>
/// <param name="Duration">How long the test took; zero when the run did not say.</param>
internal sealed record TestRecord(string Name, TestVerdict Verdict, TimeSpan Duration);
