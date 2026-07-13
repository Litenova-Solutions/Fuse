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

    [Fact]
    public void Load_DeserializesAllSupportedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "fuse.json"),
            """
            {
              "directory": "./src",
              "output": "./out",
              "name": "custom",
              "format": "json",
              "tokenizer": "cl100k_base",
              "noManifest": true,
              "provenance": true,
              "gitStats": true,
              "maxTokens": 1000,
              "splitTokens": 500,
              "recursive": false,
              "includeMetadata": true
            }
            """);

        var loaded = FuseConfigLoader.Load(root);

        Assert.NotNull(loaded);
        Assert.Equal("./src", loaded!.Directory);
        Assert.Equal("./out", loaded.Output);
        Assert.Equal("custom", loaded.Name);
        Assert.Equal("json", loaded.Format);
        Assert.Equal("cl100k_base", loaded.Tokenizer);
        Assert.True(loaded.NoManifest);
        Assert.True(loaded.Provenance);
        Assert.True(loaded.GitStats);
        Assert.Equal(1000, loaded.MaxTokens);
        Assert.Equal(500, loaded.SplitTokens);
        Assert.False(loaded.Recursive);
        Assert.True(loaded.IncludeMetadata);
    }

    [Fact]
    public void Load_InvalidFuseJson_WritesWarningToStderr()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "fuse.json");
        File.WriteAllText(configPath, "{ invalid json }");

        var stderr = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(stderr);
            var loaded = FuseConfigLoader.Load(root);

            Assert.Null(loaded);
            var output = stderr.ToString();
            Assert.Contains("Warning:", output);
            Assert.Contains(configPath, output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Load_InvalidFuserc_WritesWarningToStderr()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, ".fuserc");
        File.WriteAllText(configPath, "{ invalid json }");

        var stderr = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(stderr);
            var loaded = FuseConfigLoader.Load(root);

            Assert.Null(loaded);
            var output = stderr.ToString();
            Assert.Contains("Warning:", output);
            Assert.Contains(configPath, output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Load_MissingConfig_DoesNotWriteToStderr()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var stderr = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(stderr);
            var loaded = FuseConfigLoader.Load(root);

            Assert.Null(loaded);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
