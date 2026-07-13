namespace Fuse.Cli.Mcp;

/// <summary>
///     One candidate in a <c>fuse_test</c> race, as parsed from the tool's <c>candidates</c> JSON argument (F2).
///     Each candidate is a proposed single-file edit (the shipped <c>fuse_check</c> contract), optionally labeled;
///     a blank <see cref="Id" /> is filled with the candidate's position so every verdict is attributable.
/// </summary>
/// <param name="Id">The caller's label for this candidate (optional; defaults to its 1-based position).</param>
/// <param name="File">The repo-relative path of the file this candidate changes.</param>
/// <param name="Content">The proposed full new content of that file.</param>
public sealed record RaceCandidateInput(string? Id, string File, string Content);
