using Fuse.Cli.Services;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S1: the process-wide resident-workspace provider. Warming a root builds it once and holds the resident
// workspace so reads for that root are resident-grade; an unwarmed root reports store-backed (null). The test
// warms a real temp project (guarded to skip when the SDK cannot build) and exercises the provider surface.
public sealed class ResidentWorkspaceRegistryTests
{
    [Fact]
    public async Task Warm_makes_a_root_resident_and_unwarmed_roots_stay_store_backed()
    {
        var work = Path.Combine(Path.GetTempPath(), "fuse-resident-reg-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(work, "Widget.cs"),
                "namespace Sample; public sealed class Widget { public int Spin() => 42; }");

            using var registry = new ResidentWorkspaceRegistry();
            var root = Path.GetFullPath(work);

            // Before warming, the root is store-backed (no resident workspace, no build triggered by a read).
            Assert.Null(registry.DescribeResident(root));

            var warmed = await registry.WarmAsync(root, CancellationToken.None);
            if (!warmed)
                return; // The SDK could not build here; nothing to validate.

            Assert.NotNull(registry.DescribeResident(root));
            // A second warm is idempotent.
            Assert.True(await registry.WarmAsync(root, CancellationToken.None));

            // Resident-grade overlay check for the warmed root; a different root stays store-backed.
            var check = registry.TryCheckOverlay(root, "Widget.cs",
                "namespace Sample; public sealed class Widget { public int Spin() => 7; }", CancellationToken.None);
            Assert.NotNull(check);
            Assert.Null(registry.DescribeResident(Path.Combine(Path.GetTempPath(), "unwarmed")));

            // A batch applied through the registry advances the warmed root's revision.
            var before = registry.DescribeResident(root)!.AsOf;
            var helper = Path.Combine(work, "Helper.cs");
            await File.WriteAllTextAsync(helper, "namespace Sample; public static class Helper { public static int Base() => 1; }");
            var result = registry.ApplyBatch(root, [new WorkspaceFileChange(FileChangeKind.Created, helper)], CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Added);
            Assert.NotEqual(before, registry.DescribeResident(root)!.AsOf);

            // ApplyBatch for an unwarmed root is a no-op (null).
            Assert.Null(registry.ApplyBatch(Path.Combine(Path.GetTempPath(), "unwarmed"), [], CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
