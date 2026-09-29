using Fuse.Graph;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

/// <summary>
///     The loader as a background load and a request share it. A request that finds a project loaded folds it into the
///     views only when the loader says a background load opened it, so that mark has to be there whenever the project is.
/// </summary>
public class ProjectLoaderTests
{
    [Fact]
    public async Task A_background_load_is_marked_before_its_project_shows_as_loaded()
    {
        using var repo = FixtureRepo.CreateStandard();
        ProjectLoader? loader = null;
        ProjectNode? lib = null;
        var seen = new List<(bool Loaded, bool Marked)>();
        // The loader logs each project it opened while it still holds its load lock, after the project shows as loaded:
        // a request that ran at that moment would find the project open and ask whether a background load opened it.
        using (loader = new ProjectLoader(repo.Root, message =>
               {
                   if (lib is not null && message.StartsWith("loaded ", StringComparison.Ordinal))
                       seen.Add((loader!.IsLoaded(lib), loader.TakePreloaded()));
               }))
        {
            await loader.EvaluateAsync(TestContext.Current.CancellationToken);
            lib = loader.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;

            await loader.PreloadAsync(lib, TestContext.Current.CancellationToken);

            Assert.Equal([(true, true)], seen);
        }
    }
}
