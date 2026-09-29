using Fuse.Paths;
using Fuse.Repo;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     A sync result the workspace did not finish acting on is followed by the next one, and the two act as one: the case
///     that does more wins, so a re-evaluation after HEAD moved is never lost to a later patch.
/// </summary>
public class SyncResultTests
{
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;
    private static readonly RepoPath Calc = Root.PathOf("Lib/Calc.cs");
    private static readonly RepoPath Report = Root.PathOf("App/Report.cs");

    [Fact]
    public void A_reevaluation_followed_by_a_patch_is_a_reevaluation_of_both_sets_of_paths()
    {
        var result = new SyncResult.Reevaluate([Calc], "HEAD moved to abc").Then(new SyncResult.Patch([Report], [Root.PathOf("Lib/Gone")]));

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(result);
        Assert.Equal("HEAD moved to abc", reevaluate.Trigger);
        Assert.Equal(new HashSet<RepoPath> { Calc, Report }, reevaluate.Paths.ToHashSet());
    }

    [Fact]
    public void A_patch_followed_by_a_reload_is_a_reload_and_a_reload_followed_by_a_reevaluation_is_a_reevaluation()
    {
        var reload = Assert.IsType<SyncResult.Reload>(new SyncResult.Patch([Calc], []).Then(new SyncResult.Reload([Report], "301 changed files")));
        Assert.Equal("301 changed files", reload.Trigger);
        Assert.Equal(2, reload.Paths.Count);

        var reevaluate = Assert.IsType<SyncResult.Reevaluate>(reload.Then(new SyncResult.Reevaluate([], "Lib/Lib.csproj")));
        Assert.Equal("Lib/Lib.csproj", reevaluate.Trigger);
        Assert.Equal(2, reevaluate.Paths.Count);
    }

    [Fact]
    public void Two_reevaluations_keep_the_later_trigger()
    {
        var result = new SyncResult.Reevaluate([], "HEAD moved to abc").Then(new SyncResult.Reevaluate([], "HEAD moved to def"));

        Assert.Equal("HEAD moved to def", Assert.IsType<SyncResult.Reevaluate>(result).Trigger);
    }

    [Fact]
    public void Two_patches_keep_the_paths_and_the_vanished_directories_of_both_once()
    {
        var gone = Root.PathOf("Lib/Gone");
        var result = new SyncResult.Patch([Calc], [gone]).Then(new SyncResult.Patch([Calc, Report], [gone, Root.PathOf("App/Old")]));

        var patch = Assert.IsType<SyncResult.Patch>(result);
        Assert.Equal(new HashSet<RepoPath> { Calc, Report }, patch.Paths.ToHashSet());
        Assert.Equal([gone, Root.PathOf("App/Old")], patch.VanishedDirectories);
    }
}
