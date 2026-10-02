namespace Fuse.Evals;

/// <summary>
///     One case of a suite's plan: its place in the plan and the edits that make it. An edit whose new text is null deletes
///     its file. The plan is fixed by the repository's tree, the seed and the requested count, so every job that runs a
///     part of a suite works on the same cases.
/// </summary>
internal sealed record PlannedCase(int Index, List<FileEdit> Edits);
