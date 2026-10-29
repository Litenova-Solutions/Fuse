using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     A platform project's .NET Framework target runs through `dotnet test` for its framework alone, while its other
///     targets start directly. The framework comes from the build output path, so a path that does not name one sends the
///     whole project through `dotnet test` instead.
/// </summary>
public class TargetFrameworkPathTests
{
    [Theory]
    [InlineData("src/NodaTime.Test/bin/Release/net48/NodaTime.Test.dll", "net48")]
    [InlineData("src/NodaTime.Test/bin/Debug/net8.0/NodaTime.Test.dll", "net8.0")]
    [InlineData("src/App.Tests/bin/Release/net472/win-x64/App.Tests.dll", "net472")]
    public void The_framework_is_the_segment_after_the_configuration(string target, string expected) =>
        Assert.Equal(expected, TestOperation.FrameworkOf(Path.Combine(Path.GetTempPath(), target)));

    [Theory]
    [InlineData("src/App.Tests/out/App.Tests.dll")]
    [InlineData("src/App.Tests/bin/App.Tests.dll")]
    [InlineData("src/App.Tests/bin/Release/custom/App.Tests.dll")]
    public void A_path_outside_the_default_layout_names_no_framework(string target) =>
        Assert.Null(TestOperation.FrameworkOf(Path.Combine(Path.GetTempPath(), target)));
}
