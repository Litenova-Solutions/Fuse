namespace Fuse.Analysis.Changes;

/// <summary>
///     Options for git change-scoped fusion.
/// </summary>
/// <param name="Since">Git ref: branch name, commit hash, or HEAD~N expression.</param>
/// <param name="IncludeDependents">Whether to include first-degree dependents of changed files.</param>
public sealed record ChangeOptions(string Since, bool IncludeDependents = true);
