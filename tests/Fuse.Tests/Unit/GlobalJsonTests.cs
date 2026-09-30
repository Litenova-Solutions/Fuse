using Fuse.Dotnet;

namespace Fuse.Tests.Unit;

/// <summary>
///     Whether <c>dotnet test</c> runs on Microsoft.Testing.Platform, which the nearest <c>global.json</c> up to the
///     repository root decides, as the dotnet CLI reads it.
/// </summary>
public class GlobalJsonTests
{
    private const string TestingPlatform = """{ "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }""";

    [Fact]
    public void A_global_json_that_sets_the_runner_selects_microsoft_testing_platform() =>
        InRepository(root =>
        {
            File.WriteAllText(Path.Combine(root, "global.json"), TestingPlatform);

            Assert.True(GlobalJson.UsesTestingPlatform(Directory.CreateDirectory(Path.Combine(root, "tests", "A")).FullName, root));
        });

    [Fact]
    public void A_global_json_that_only_mentions_the_platform_does_not() =>
        InRepository(root =>
        {
            File.WriteAllText(Path.Combine(root, "global.json"), """{ "sdk": { "version": "10.0.100" }, "msbuild-sdks": { "Microsoft.Testing.Platform.MSBuild": "1.0.0" } }""");

            Assert.False(GlobalJson.UsesTestingPlatform(root, root));
        });

    [Fact]
    public void The_nearest_global_json_decides() =>
        InRepository(root =>
        {
            File.WriteAllText(Path.Combine(root, "global.json"), TestingPlatform);
            var nested = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
            File.WriteAllText(Path.Combine(nested, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");

            Assert.False(GlobalJson.UsesTestingPlatform(nested, root));
        });

    [Fact]
    public void A_global_json_above_the_repository_root_is_not_read() =>
        InRepository(outside =>
        {
            File.WriteAllText(Path.Combine(outside, "global.json"), TestingPlatform);
            var root = Directory.CreateDirectory(Path.Combine(outside, "repo")).FullName;

            Assert.False(GlobalJson.UsesTestingPlatform(root, root));
        });

    [Fact]
    public void A_global_json_that_is_not_json_does_not_select_it() =>
        InRepository(root =>
        {
            File.WriteAllText(Path.Combine(root, "global.json"), "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\"");

            Assert.False(GlobalJson.UsesTestingPlatform(root, root));
        });

    private static void InRepository(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-tests", "global-json-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
