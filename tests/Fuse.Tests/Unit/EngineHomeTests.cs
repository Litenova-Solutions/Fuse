using Fuse.Engine.Client;

namespace Fuse.Tests.Unit;

/// <summary>
///     The engine runs from a private copy of its build. A copy counts only once its <c>.complete</c> marker exists, so a
///     copy a cleanup deleted part of is made again instead of being run with files missing.
/// </summary>
public sealed class EngineHomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-engine-home-" + Guid.NewGuid().ToString("N")[..8]);

    public EngineHomeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.WriteAllText(Path.Combine(_root, "build", "fuse.dll"), "engine");
    }

    [Fact]
    public void A_copy_without_its_marker_is_made_again()
    {
        var copies = Path.Combine(_root, "copies");
        // What a cleanup that stopped halfway leaves: the directory, without the marker or the assembly.
        Directory.CreateDirectory(Path.Combine(copies, "5.2.2-abc"));

        var home = EngineLauncher.EngineHome(copies, "5.2.2/abc", () => Path.Combine(_root, "build"));

        Assert.Equal(Path.Combine(copies, "5.2.2-abc"), home);
        Assert.True(File.Exists(Path.Combine(home, ".complete")));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(home, "fuse.dll")));
        Assert.Single(Directory.EnumerateDirectories(copies));
    }

    [Fact]
    public void A_complete_copy_is_used_as_it_is()
    {
        var copies = Path.Combine(_root, "copies");
        var first = EngineLauncher.EngineHome(copies, "5.2.2/abc", () => Path.Combine(_root, "build"));

        var second = EngineLauncher.EngineHome(copies, "5.2.2/abc", () => throw new InvalidOperationException("a complete copy is not copied again"));

        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
