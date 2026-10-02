namespace Fuse.Harnesses;

/// <summary>A file <c>fuse init</c> registered Fuse in, and whether its content changed.</summary>
/// <param name="Path">The file's repository-relative path.</param>
/// <param name="Changed">False when the file already held exactly this content, so it was left as it was.</param>
internal sealed record WrittenFile(string Path, bool Changed);
