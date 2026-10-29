namespace Fuse.Evals;

/// <summary>
///     One pair where the second writer is an external writer registered with <c>fuse session</c>: writer A breaks a file
///     through the post-edit hook as its own session, and the other writer's file is registered to another session by path.
///     "Other" errors are the ones the external writer's change caused, counted over the errors A's stop answer prints.
/// </summary>
/// <param name="RenamedIn">The file where writer A renamed a public method.</param>
/// <param name="Method">The method A renamed.</param>
/// <param name="BrokenIn">The file, in another project and calling the method, where the external writer added a statement naming nothing.</param>
/// <param name="OtherWithRegistration">Other errors in A's stop answer while the external writer is registered: what A was told about its edit.</param>
/// <param name="BlockedWhileRegistered">Whether A's stop blocked while the external writer was registered.</param>
/// <param name="BlockedAfterEnd">Whether A's stop blocked after the registration ended.</param>
/// <param name="ExpiredShielding">Whether an expired registration still shields; null, because registrations expire after 4 hours, which the eval does not wait out.</param>
/// <param name="MsWithRegistration">Wall time of A's stop while the external writer was registered, in milliseconds.</param>
/// <param name="MsAfterEnd">Wall time of A's stop after the registration ended, in milliseconds.</param>
/// <param name="Note">What went wrong, or null.</param>
internal sealed record ExternalWriterPair(
    string RenamedIn,
    string Method,
    string BrokenIn,
    int OtherWithRegistration,
    bool BlockedWhileRegistered,
    bool BlockedAfterEnd,
    bool? ExpiredShielding,
    double MsWithRegistration,
    double MsAfterEnd,
    string? Note);
