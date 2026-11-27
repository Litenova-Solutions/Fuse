namespace Fuse.Protocol.Requests;

/// <summary>How many candidates a judge call scores and how many bytes of diff it holds, in one place for the client and the engine.</summary>
internal static class JudgeLimits
{
    /// <summary>How many candidates one judge call scores; past it the call is refused before anything is parsed.</summary>
    internal const int MaxCandidates = 16;

    /// <summary>How many bytes of diff one candidate holds; past it the call is refused before anything is parsed.</summary>
    internal const int MaxDiffBytes = 1024 * 1024;

    /// <summary>How many bytes of diff one judge call scores in total; past it the call is refused before anything is parsed.</summary>
    internal const int MaxTotalDiffBytes = 8 * 1024 * 1024;
}
