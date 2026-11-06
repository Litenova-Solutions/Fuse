using Fuse.Check.Model;

namespace Fuse.Tests.Fixtures;

/// <summary>
///     Two in-process engines over two copies of the same fixture repository: one checks from source and one
///     checks with summaries outside the change cone, so a test holds the two answers side by side. Every edit
///     a test makes is made in both repositories, and each check is answered by both engines.
/// </summary>
internal sealed class SummaryPair : IAsyncDisposable
{
    private SummaryPair(InProcessEngine fromSource, InProcessEngine withSummaries)
    {
        FromSource = fromSource;
        WithSummaries = withSummaries;
    }

    /// <summary>The engine that checks from source, with summaries off.</summary>
    public InProcessEngine FromSource { get; }

    /// <summary>The engine that checks with summaries outside the change cone.</summary>
    public InProcessEngine WithSummaries { get; }

    /// <summary>
    ///     Starts both engines over copies of the standard fixture, with <paramref name="arrange"/> applied to
    ///     each copy and committed, so the files it writes are part of HEAD in both repositories.
    /// </summary>
    public static async Task<SummaryPair> StartAsync(Action<FixtureRepo>? arrange = null)
    {
        var fromSource = await StartOneAsync(summaries: false, arrange);
        var withSummaries = await StartOneAsync(summaries: true, arrange);
        return new SummaryPair(fromSource, withSummaries);
    }

    private static async Task<InProcessEngine> StartOneAsync(bool summaries, Action<FixtureRepo>? arrange)
    {
        var repo = FixtureRepo.CreateStandard();
        if (arrange is not null)
        {
            arrange(repo);
            repo.Commit("fixture files");
        }

        var engine = await InProcessEngine.StartAsync(repo);
        engine.Workspace.UseSummaries = summaries;
        return engine;
    }

    /// <summary>Replaces text in the file both repositories hold, the edit a test makes in both.</summary>
    public void ReplaceBoth(string relative, string oldText, string newText)
    {
        FromSource.Repo.Replace(relative, oldText, newText);
        WithSummaries.Repo.Replace(relative, oldText, newText);
    }

    /// <summary>
    ///     Commits only the given files in both repositories, so an edit in another file stays a working-tree
    ///     edit in both and HEAD moves by the given files alone.
    /// </summary>
    public void CommitFilesBoth(string message, params string[] relatives)
    {
        CommitFiles(FromSource.Repo, message, relatives);
        CommitFiles(WithSummaries.Repo, message, relatives);
    }

    private static void CommitFiles(FixtureRepo repo, string message, string[] relatives)
    {
        string[] arguments = ["-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message, "--", .. relatives];
        FixtureRepo.Run(repo.Root.Path, "git", arguments);
    }

    /// <summary>Checks the given repository-relative files in both engines and returns the two answers.</summary>
    public async Task<(Snapshot FromSource, Snapshot WithSummaries)> CheckBothAsync(params string[] files)
    {
        var fromSource = await SnapshotAsync(FromSource, files);
        var withSummaries = await SnapshotAsync(WithSummaries, files);
        return (fromSource, withSummaries);
    }

    private static async Task<Snapshot> SnapshotAsync(InProcessEngine engine, string[] files)
    {
        var report = await engine.CheckAsync(files);
        var reach = await engine.ReachAsync(files);
        return new Snapshot(
            RenderErrors(report),
            RenderReach(reach),
            report.FilesChecked,
            string.Join(",", report.DeclarationsChangedIn),
            report.DependentProjectsChecked,
            report.CheckedWholeProjects,
            report.ErrorCount,
            report.ErrorFileCount);
    }

    /// <summary>
    ///     True when the shared state directory of <paramref name="engine"/>'s repository holds a summary
    ///     assembly, so a test can tell a check that bound against summaries from one that never wrote any.
    /// </summary>
    public static bool SummariesStored(InProcessEngine engine)
    {
        var directory = Path.Combine(engine.Repo.Root.SharedStateDirectory, "summaries");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();
    }

    /// <summary>One check's answer in a form two repositories can be compared by: repository-relative paths only.</summary>
    /// <param name="Errors">Each introduced error, with its cause, its generated file and whether an analyzer reported it.</param>
    /// <param name="Reach">The kind of reach (none, precise or broad) and the files it names, in path order.</param>
    /// <param name="FilesChecked">How many files the check bound.</param>
    /// <param name="DeclarationsChangedIn">The projects that own a target with a declaration change, joined in order.</param>
    /// <param name="DependentProjectsChecked">How many dependents of those projects the check searched and bound.</param>
    /// <param name="CheckedWholeProjects">True when whole projects were bound.</param>
    /// <param name="ErrorCount">How many errors were found, counted before the cap.</param>
    /// <param name="ErrorFileCount">How many files those errors are in, counted before the cap.</param>
    public sealed record Snapshot(
        string Errors,
        string Reach,
        int FilesChecked,
        string DeclarationsChangedIn,
        int DependentProjectsChecked,
        bool CheckedWholeProjects,
        int ErrorCount,
        int ErrorFileCount);

    private static string RenderErrors(CheckResult report) =>
        string.Join("\n", report.Errors.Select(e => $"{e.Error} | cause={(e.Cause is { } cause ? $"{cause.GetType().Name}: {cause.Declaration}" : "-")} | generated={e.GeneratedFile ?? "-"} | analyzer={e.Error.FromAnalyzer}"));

    private static string RenderReach(Reach reach) => reach switch
    {
        Reach.None => "none",
        Reach.Broad broad => "broad:" + string.Join(",", broad.Files.Select(f => f.Relative).Order(StringComparer.Ordinal)),
        Reach.Precise precise => "precise:" + string.Join(",", precise.Causes.Keys.Select(f => f.Relative).Order(StringComparer.Ordinal)),
        _ => "unknown",
    };

    public async ValueTask DisposeAsync()
    {
        await FromSource.DisposeAsync();
        await WithSummaries.DisposeAsync();
    }
}
