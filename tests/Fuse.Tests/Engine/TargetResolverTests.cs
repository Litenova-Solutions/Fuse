using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Which files a check binds first. Only a source file an evaluated project owns can gain a compiler error, so every
///     other file a scope covers is dropped before anything loads.
/// </summary>
public class TargetResolverTests
{
    [Fact]
    public async Task Named_files_resolve_to_the_sources_an_evaluated_project_owns_each_once()
    {
        await using var engine = await EngineHarness.StartAsync();
        var resolver = new TargetResolver(engine.Workspace);
        // The same file relative and absolute, a file in build output, a source no project owns, and a file that is no source.
        var scope = new CheckScope.Files(["Lib/Calc.cs", engine.Repo.Full("Lib/Calc.cs"), "Lib/obj/Generated.cs", "Loose.cs", "App/notes.txt"]);

        Assert.Equal(
            [engine.Repo.Full("Lib/Calc.cs"), engine.Repo.Full("Lib/Calc.cs"), engine.Repo.Full("Lib/obj/Generated.cs"), engine.Repo.Full("Loose.cs"), engine.Repo.Full("App/notes.txt")],
            resolver.NamedPaths(scope));
        Assert.Equal([engine.Repo.Full("Lib/Calc.cs")], resolver.Resolve(scope));
    }

    [Fact]
    public async Task Every_change_resolves_to_the_changed_sources_an_evaluated_project_owns()
    {
        await using var engine = await EngineHarness.StartAsync();
        var resolver = new TargetResolver(engine.Workspace);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        engine.Repo.Write("App/Extra.cs", "namespace App;\n\npublic static class Extra\n{\n}\n");
        engine.Repo.Write("Loose.cs", "public static class Loose\n{\n}\n");
        engine.Repo.Write("App/notes.txt", "not a source\n");
        var scope = new CheckScope.AllChanges();

        // The change tracker names every change, so the sync is given no paths of its own.
        Assert.Empty(resolver.NamedPaths(scope));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        await engine.Workspace.SyncAsync(resolver.NamedPaths(scope), TestContext.Current.CancellationToken);

        Assert.Equal(
            [engine.Repo.Full("App/Extra.cs"), engine.Repo.Full("Lib/Calc.cs")],
            resolver.Resolve(scope).Order(StringComparer.Ordinal));
    }
}
