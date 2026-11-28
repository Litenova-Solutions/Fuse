namespace Fuse.Dotnet.Test;

/// <summary>One failed test with its message and the stack frames inside the repository.</summary>
internal sealed record TestFailure(string Name, string Message, IReadOnlyList<string> Frames);
