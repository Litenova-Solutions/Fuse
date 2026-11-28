using Fuse.Failures;
using Fuse.Protocol.Check;
namespace Fuse.Protocol.Requests;

internal abstract partial record EngineRequest
{
    /// <summary>The errors that every change since HEAD introduced, in the changed files and in the files they reach.</summary>
    /// <param name="WaitForLoad">
    ///     True to wait for the engine to finish loading the repository; false to be answered at once with
    ///     <see cref="ErrorCode.Loading"/> while it loads.
    /// </param>
    public sealed record CheckChanges(bool WaitForLoad) : EngineRequest
    {
        /// <summary>
        ///     The session the answer is for, as its harness identifies it, or null to answer with every error. The stop
        ///     hook sends the session that is stopping, which is told the errors its own edits cause and those no session's
        ///     edits are credited with, and not those other sessions' edits cause without it.
        /// </summary>
        public string? Session { get; init; }

        /// <summary>
        ///     Whether the check only answers or also marks <see cref="Session"/> stopped. The stop hook sends
        ///     <see cref="SessionCheckMode.Stop"/>: a subagent that has stopped writes no more, so from then on its
        ///     parent session counts its edits as its own. A line without it reads as a check.
        /// </summary>
        public SessionCheckMode Mode { get; init; }

        /// <summary>
        ///     How many errors the answer holds at most; 0 keeps the default cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }

        /// <summary>True to stamp the answer with the tree the check ended on, which <c>fuse check</c> prints. A line without it reads as false.</summary>
        public bool StampTree { get; init; }

        /// <summary>True to attach <see cref="ReportedRepairPacket"/> details for <c>--brief</c>. A line without it reads as false.</summary>
        public bool Brief { get; init; }
    }

    /// <summary>The errors that the changes to the named files introduced, in those files and in the files they reach.</summary>
    /// <param name="Files">
    ///     Absolute or repository-relative paths, usually the files an agent has just edited. The sync reads them from disk
    ///     even before the file watcher reports them, so a check that follows an edit at once still sees it.
    /// </param>
    /// <param name="WaitForLoad">
    ///     True to wait for the engine to finish loading the repository; false to be answered at once with
    ///     <see cref="ErrorCode.Loading"/> while it loads. A line without it reads as false.
    /// </param>
    public sealed record CheckFiles(IReadOnlyList<string> Files, bool WaitForLoad) : EngineRequest
    {
        /// <summary>The named files. A line without the list reads as one that names none, which checks nothing.</summary>
        public IReadOnlyList<string> Files { get; init; } = Files ?? [];

        /// <summary>
        ///     The session that wrote <see cref="Files"/>, as its harness identifies it, or null when the client knows none.
        ///     The engine records that the session wrote the files, and answers with the errors that session's edits cause,
        ///     leaving out those other sessions' edits caused without it. The post-edit hook sends one, and so does a check run with FUSE_SESSION set.
        /// </summary>
        public string? Session { get; init; }

        /// <summary>
        ///     True to leave out the analyzers that read the whole compilation (compilation-wide analyzers), which the
        ///     check then names. The post-edit hook sends true, so an edit is answered in seconds; the stop hook, which
        ///     checks every change, runs them. A line without it reads as false, which runs every analyzer.
        /// </summary>
        public bool LeaveOutCompilationWide { get; init; }

        /// <summary>
        ///     How many errors the answer holds at most; 0 keeps the default cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }

        /// <summary>True to stamp the answer with the tree the check ended on, which <c>fuse check</c> prints. A line without it reads as false.</summary>
        public bool StampTree { get; init; }

        /// <summary>True to attach <see cref="ReportedRepairPacket"/> details for <c>--brief</c>. A line without it reads as false.</summary>
        public bool Brief { get; init; }

        /// <summary>
        ///     True on post-edit hook calls so body-only edits can use fast span-level binding without running
        ///     whole-file analyzers. A line without it reads as false.
        /// </summary>
        public bool InteractiveFast { get; init; }
    }

    /// <summary>The errors that the changes under the named paths introduced, in those files and in the files they reach.</summary>
    /// <param name="Paths">Absolute file and directory paths and absolute glob patterns.</param>
    /// <param name="WaitForLoad">True to wait for the engine to finish loading; false to be answered at once with <see cref="ErrorCode.Loading"/> while it loads.</param>
    public sealed record CheckPaths(string[] Paths, bool WaitForLoad) : EngineRequest
    {
        /// <summary>The named paths. A line without the list reads as one that names none, which checks nothing.</summary>
        public string[] Paths { get; init; } = Paths ?? [];

        /// <summary>
        ///     How many errors the answer holds at most; 0 keeps the default cap. A line without it reads as 0.
        /// </summary>
        public int MaxErrors { get; init; }

        /// <summary>True to stamp the answer with the tree the check ended on, which <c>fuse check</c> prints. A line without it reads as false.</summary>
        public bool StampTree { get; init; }

        /// <summary>The session the check is answered to, or null to report every error. A line without it reads as null.</summary>
        public string? Session { get; init; }

        /// <summary>True to attach <see cref="ReportedRepairPacket"/> details for <c>--brief</c>. A line without it reads as false.</summary>
        public bool Brief { get; init; }
    }
}
