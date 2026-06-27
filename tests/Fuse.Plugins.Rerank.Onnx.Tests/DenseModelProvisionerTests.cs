using Fuse.Plugins.Rerank.Onnx;

namespace Fuse.Plugins.Rerank.Onnx.Tests;

// A1: dense is on by default (the no-model floor is retired). FUSE_DENSE is now an opt-out, and provisioning is
// offline-safe: a disabled or already-present model is a no-op, and these checks never touch the network.
public sealed class DenseModelProvisionerTests
{
    [Fact]
    public void IsDenseEnabled_DefaultsTrue_WhenUnset()
    {
        WithDense(null, () => Assert.True(DenseModelProvisioner.IsDenseEnabled));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("no")]
    [InlineData("off")]
    public void IsDenseEnabled_False_OnExplicitOptOut(string value)
    {
        WithDense(value, () => Assert.False(DenseModelProvisioner.IsDenseEnabled));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("anything-else")]
    public void IsDenseEnabled_True_OnAnyNonFalsyValue(string value)
    {
        WithDense(value, () => Assert.True(DenseModelProvisioner.IsDenseEnabled));
    }

    [Fact]
    public async Task EnsureModelAsync_Disabled_IsNoOpAndFetchesNothing()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fuse-provision", Guid.NewGuid().ToString("N"));
        var originalData = Environment.GetEnvironmentVariable("FUSE_USER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FUSE_USER_DATA", temp);
            var present = await WithDenseAsync("0", () =>
                DenseModelProvisioner.EnsureModelAsync(progress: null, logger: null, CancellationToken.None));

            Assert.False(present);
            Assert.False(RerankModelLocator.IsModelPresent());
            Assert.False(Directory.Exists(temp), "an opt-out must not fetch or create the model cache");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_USER_DATA", originalData);
        }
    }

    private static void WithDense(string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable("FUSE_DENSE");
        try
        {
            Environment.SetEnvironmentVariable("FUSE_DENSE", value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_DENSE", original);
        }
    }

    private static async Task<T> WithDenseAsync<T>(string? value, Func<Task<T>> body)
    {
        var original = Environment.GetEnvironmentVariable("FUSE_DENSE");
        try
        {
            Environment.SetEnvironmentVariable("FUSE_DENSE", value);
            return await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_DENSE", original);
        }
    }
}
