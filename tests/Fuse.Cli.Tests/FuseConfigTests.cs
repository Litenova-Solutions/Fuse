using Fuse.Cli.Configuration;

namespace Fuse.Cli.Tests;

public sealed class FuseConfigTests
{
    [Fact]
    public void Merge_ConfigValuesApplyWhenCliUsesDefaults()
    {
        var config = new FuseConfig
        {
            Output = "./from-config",
            Tokenizer = "cl100k_base",
            NoManifest = true,
            Provenance = true,
            Format = "json",
        };

        var cli = new FuseCliOptions();
        var merged = FuseConfigMerger.Merge(config, cli);

        Assert.Equal("./from-config", merged.Output);
        Assert.Equal("cl100k_base", merged.Tokenizer);
        Assert.True(merged.NoManifest);
        Assert.True(merged.Provenance);
        Assert.Equal("json", merged.Format);
    }

    [Fact]
    public void Merge_CliFlagsOverrideConfig()
    {
        var config = new FuseConfig { Format = "json", Tokenizer = "cl100k_base" };
        var cli = new FuseCliOptions { Format = "markdown", Tokenizer = "o200k_base" };
        var merged = FuseConfigMerger.Merge(config, cli);

        Assert.Equal("markdown", merged.Format);
        Assert.Equal("o200k_base", merged.Tokenizer);
    }

    [Fact]
    public void Load_FindsNearestFuseJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-config-test", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "fuse.json"), """{"directory":"."}""");

        var loaded = FuseConfigLoader.Load(nested);

        Assert.NotNull(loaded);
        Assert.Equal(".", loaded!.Directory);
    }
}
