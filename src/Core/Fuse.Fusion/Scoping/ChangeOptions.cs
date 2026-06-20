namespace Fuse.Fusion.Scoping;

/// <summary>
///     Options for git change-scoped fusion.
/// </summary>
/// <param name="Since">Git ref: branch name, commit hash, or HEAD~N expression.</param>
/// <param name="IncludeDependents">Whether to include first-degree dependents of changed files.</param>
/// <param name="Review">
///     Whether to prepend a review map: each changed file's unified diff hunks paired with its direct callers.
///     Pairs naturally with <see cref="IncludeDependents" /> so those callers are also emitted in full.
/// </param>
public sealed record ChangeOptions(string Since, bool IncludeDependents = true, bool Review = false);
