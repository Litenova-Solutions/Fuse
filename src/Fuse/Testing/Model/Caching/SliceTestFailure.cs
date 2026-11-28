namespace Fuse.Testing.Model.Caching;

/// <summary>One failed test stored in a slice test cache entry.</summary>
/// <param name="Name">Fully qualified test name.</param>
/// <param name="Message">Failure message.</param>
/// <param name="Frames">Stack frames inside the repository.</param>
internal sealed record SliceTestFailure(string Name, string Message, IReadOnlyList<string> Frames);
