using Fuse.BuildCaptureWorker;
using Xunit;

namespace Fuse.BuildCaptureWorker.Tests;

public sealed class LazyHeldComplogTests
{
    [Fact]
    public async Task Lazy_held_log_rehydrates_only_the_project_that_owns_each_request()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-r53", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "First");
        var second = Path.Combine(root, "Second");
        var complog = Path.Combine(root, "capture.complog");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(first, "First.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(first, "First.cs"), "namespace Fixture; public class First { public int Value() => 1; }");
            await File.WriteAllTextAsync(Path.Combine(second, "Second.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../First/First.csproj\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(second, "Second.cs"), "using Fixture; namespace Fixture; public class Second { public int Value() => new First().Value(); }");

            var rehydrator = new BuildCaptureRehydrator();
            var capture = await rehydrator.ExportCompilerLogAsync(Path.Combine(second, "Second.csproj"), complog, TimeSpan.FromMinutes(5), CancellationToken.None, root);
            if (!capture.Succeeded)
            {
                Assert.False(string.IsNullOrWhiteSpace(capture.Reason));
                return;
            }

            using var held = rehydrator.RehydrateLazyHeld(complog, CancellationToken.None);
            Assert.Equal(0, held.RehydratedProjectCount);
            var secondResult = rehydrator.CheckLazyHeld(held, "Second/Second.cs", "using Fixture; namespace Fixture; public class Second { public int Value() => new First().Value(); }", CancellationToken.None);
            Assert.True(secondResult.IsClean);
            Assert.Equal(1, held.RehydratedProjectCount);
            var firstResult = rehydrator.CheckLazyHeld(held, "First/First.cs", "namespace Fixture; public class First { public int Value() => 2; }", CancellationToken.None);
            Assert.True(firstResult.IsClean);
            Assert.Equal(2, held.RehydratedProjectCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
