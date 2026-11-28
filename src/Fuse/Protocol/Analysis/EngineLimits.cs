namespace Fuse.Protocol.Analysis;

/// <summary>The tree-size limits the engine counts by, carried on its status so the client prints the same numbers.</summary>
/// <param name="ReloadAt">Past this many changed sources in one sync a sync reloads the projects.</param>
/// <param name="KeptAnswerAt">Past this many changed files a check skips its kept answer.</param>
/// <param name="WholeProjectAt">Past this many files in reach of a change a check binds whole projects.</param>
/// <param name="TouchedLimit">Past this many distinct touched paths the next sync re-evaluates every project.</param>
internal sealed record EngineLimits(int ReloadAt, int KeptAnswerAt, int WholeProjectAt, int TouchedLimit)
{
    /// <summary>The limits an engine of an earlier build answered without, and the limits printed with no engine running.</summary>
    internal static EngineLimits Default => new(ReloadAt: 300, KeptAnswerAt: 500, WholeProjectAt: 500, TouchedLimit: 2000);
}
