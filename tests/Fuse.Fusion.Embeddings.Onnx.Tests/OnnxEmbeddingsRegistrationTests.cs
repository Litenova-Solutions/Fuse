using Fuse.Fusion.Embeddings.Onnx;
using Fuse.Fusion.Embeddings.Onnx.Extensions;
using Fuse.Fusion.Retrieval;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Embeddings.Onnx.Tests;

public sealed class OnnxEmbeddingsRegistrationTests : IDisposable
{
    public OnnxEmbeddingsRegistrationTests() =>
        Environment.SetEnvironmentVariable(OnnxEmbeddingsServiceCollectionExtensions.EnableVariable, null);

    [Fact]
    public void ResolveChoice_ExplicitFlagWins()
    {
        Environment.SetEnvironmentVariable(OnnxEmbeddingsServiceCollectionExtensions.EnableVariable, "0");
        Assert.True(OnnxEmbeddingsServiceCollectionExtensions.ResolveChoice(true));
        Assert.False(OnnxEmbeddingsServiceCollectionExtensions.ResolveChoice(false));
    }

    [Fact]
    public void ResolveChoice_EnvUnset_IsBuildDefault()
    {
        Assert.Null(OnnxEmbeddingsServiceCollectionExtensions.ResolveChoice(null));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void ResolveChoice_ReadsEnvWhenNoExplicitFlag(string env, bool expected)
    {
        Environment.SetEnvironmentVariable(OnnxEmbeddingsServiceCollectionExtensions.EnableVariable, env);
        Assert.Equal(expected, OnnxEmbeddingsServiceCollectionExtensions.ResolveChoice(null));
    }

    [Fact]
    public void AddFuseOnnxEmbeddings_Off_LeavesHashingDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingModel>(new HashingEmbeddingModel());
        services.AddFuseOnnxEmbeddings(explicitFlag: false);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<HashingEmbeddingModel>(provider.GetRequiredService<IEmbeddingModel>());
    }

    [Fact]
    public void AddFuseOnnxEmbeddings_On_ButModelUnavailable_FallsBackToHashing()
    {
        // Point the sideload variable at an empty directory: the model cannot resolve and no network is used,
        // so the run completes on the hashing fallback.
        var empty = Path.Combine(Path.GetTempPath(), "fuse-onnx-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, empty);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEmbeddingModel>(new HashingEmbeddingModel());
            services.AddFuseOnnxEmbeddings(explicitFlag: true);

            using var provider = services.BuildServiceProvider();
            Assert.IsType<HashingEmbeddingModel>(provider.GetRequiredService<IEmbeddingModel>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, null);
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void AddFuseOnnxEmbeddings_On_WithAvailableModel_SelectsOnnx()
    {
        var modelDir = TestModel.LocalModelDirectory();
        if (modelDir is null)
            return; // model asset absent (CI without the download): real selection is not exercised.

        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, modelDir);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEmbeddingModel>(new HashingEmbeddingModel());
            services.AddFuseOnnxEmbeddings(explicitFlag: true);

            using var provider = services.BuildServiceProvider();
            Assert.IsType<OnnxEmbeddingModel>(provider.GetRequiredService<IEmbeddingModel>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, null);
        }
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(OnnxEmbeddingsServiceCollectionExtensions.EnableVariable, null);
}
