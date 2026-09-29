using Fuse.Operations;

namespace Fuse.Tests.Unit;

/// <summary>
///     Harnesses and scripts read fuse's exit code, and it derives from the outcome alone, so each outcome is pinned to
///     its number here.
/// </summary>
public class OperationResultTests
{
    [Fact]
    public void Each_outcome_has_its_exit_code()
    {
        Assert.Equal(0, new OperationResult(Outcome.Clean, "").ExitCode);
        Assert.Equal(1, new OperationResult(Outcome.ProblemsFound, "").ExitCode);
        Assert.Equal(2, new OperationResult(Outcome.Unanswered, "").ExitCode);
    }
}
