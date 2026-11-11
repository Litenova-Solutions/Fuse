using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     The session a caller names in <c>FUSE_SESSION</c>: a worker without hooks names itself that way to
///     <c>fuse check</c>, <c>fuse test</c> and <c>fuse mcp</c>, and a value no session can be recorded under names
///     no session, so the command answers as one with no session does.
/// </summary>
public class SessionEnvironmentTests
{
    [Theory]
    [InlineData("worker-7")]
    [InlineData("w1")]
    [InlineData("claude:a/agent-1")]
    public void A_session_named_in_the_environment_is_the_session(string value) =>
        Assert.Equal(value, SessionOperation.SessionFromValue(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void A_value_no_session_can_be_recorded_under_names_no_session(string? value) =>
        Assert.Null(SessionOperation.SessionFromValue(value));

    [Fact]
    public void A_session_longer_than_the_limit_names_no_session()
    {
        Assert.Equal(new string('a', 256), SessionOperation.SessionFromValue(new string('a', 256)));
        Assert.Null(SessionOperation.SessionFromValue(new string('a', 257)));
    }

    [Fact]
    public void The_environment_session_comes_from_FUSE_SESSION()
    {
        var saved = Environment.GetEnvironmentVariable(SessionOperation.SessionVariable);
        try
        {
            Environment.SetEnvironmentVariable(SessionOperation.SessionVariable, "worker-7");
            Assert.Equal("worker-7", SessionOperation.EnvironmentSession);

            Environment.SetEnvironmentVariable(SessionOperation.SessionVariable, null);
            Assert.Null(SessionOperation.EnvironmentSession);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionOperation.SessionVariable, saved);
        }
    }
}
