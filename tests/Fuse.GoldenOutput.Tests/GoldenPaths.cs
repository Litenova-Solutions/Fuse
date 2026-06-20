using System.Reflection;

namespace Fuse.GoldenOutput.Tests;

internal static class GoldenPaths
{
    public static string RepoRoot { get; } = ResolveRepoRoot();

    public static string SampleShopFixture { get; } =
        Path.Combine(RepoRoot, "tests", "fixtures", "SampleShop");

    public static string ExpectedDirectory { get; } =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "expected");

    private static string ResolveRepoRoot()
    {
        var start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var current = new DirectoryInfo(start);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Fuse.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing Fuse.slnx.");
    }
}
