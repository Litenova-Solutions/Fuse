namespace Fuse.Harnesses;

/// <summary>A file <c>fuse init</c> registered Fuse in, or <c>fuse uninit</c> removed Fuse from, and what happened to it.</summary>
/// <param name="Path">The file's repository-relative path.</param>
/// <param name="Changed">False when the file already held exactly this content, so it was left as it was.</param>
/// <param name="Deleted">True when the file was deleted, because it belonged to Fuse or held nothing else.</param>
internal sealed record WrittenFile(string Path, bool Changed, bool Deleted = false);
