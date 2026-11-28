using Fuse.Testing.Model;
namespace Fuse.Tests.Unit;

public class TargetFrameworkTests
{
    [Theory]
    [InlineData("net9.0", "Net", "9.0")]
    [InlineData("net10.0", "Net", "10.0")]
    [InlineData("net8.0-windows", "Net", "8.0")]
    [InlineData("netstandard2.0", "NetStandard", "2.0")]
    [InlineData("netstandard2.1", "NetStandard", "2.1")]
    [InlineData("netcoreapp3.1", "Net", "3.1")]
    [InlineData("net462", "NetFramework", "4.6.2")]
    [InlineData("net48", "NetFramework", "4.8")]
    [InlineData("net481", "NetFramework", "4.8.1")]
    public void Parses_a_framework_moniker(string text, string family, string version)
    {
        Assert.Equal(
            new TargetFramework(Enum.Parse<TargetFramework.FamilyKind>(family), Version.Parse(version)),
            TargetFramework.Parse(text));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("net")]
    [InlineData("netfoo")]
    [InlineData("")]
    public void Rejects_text_that_names_no_framework(string text)
    {
        Assert.Null(TargetFramework.Parse(text));
    }

    [Theory]
    [InlineData("net9.0", "Net", "9.0")]
    [InlineData("debug_net9.0", "Net", "9.0")]
    [InlineData("release_net8.0", "Net", "8.0")]
    [InlineData("debug_net462", "NetFramework", "4.6.2")]
    public void Parses_a_build_output_folder(string folder, string family, string version)
    {
        Assert.Equal(
            new TargetFramework(Enum.Parse<TargetFramework.FamilyKind>(family), Version.Parse(version)),
            TargetFramework.ParseFolder(folder));
    }

    [Fact]
    public void A_runtime_identifier_folder_names_no_framework()
    {
        Assert.Null(TargetFramework.ParseFolder("win-x64"));
    }

    [Theory]
    // The Polly shape: the net9.0 tests load the net8.0 library builds, which are the nearest below them.
    [InlineData("net9.0", "net8.0")]
    [InlineData("net9.0", "net9.0")]
    [InlineData("net9.0", "netstandard2.0")]
    [InlineData("net8.0", "netstandard2.1")]
    [InlineData("net462", "netstandard2.0")]
    [InlineData("netstandard2.0", "netstandard1.6")]
    public void Accepts_a_dependency_its_runtime_loads(string requester, string candidate)
    {
        Assert.True(TargetFramework.Parse(requester)!.Accepts(TargetFramework.Parse(candidate)!));
    }

    [Theory]
    [InlineData("net9.0", "net10.0")]
    [InlineData("net462", "netstandard2.1")]
    [InlineData("net462", "net8.0")]
    [InlineData("net8.0", "net462")]
    [InlineData("netstandard2.0", "netstandard2.1")]
    [InlineData("netstandard2.0", "net8.0")]
    public void Rejects_a_dependency_its_runtime_does_not_load(string requester, string candidate)
    {
        Assert.False(TargetFramework.Parse(requester)!.Accepts(TargetFramework.Parse(candidate)!));
    }
}
