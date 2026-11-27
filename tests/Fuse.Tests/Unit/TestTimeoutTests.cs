using Fuse.Dotnet.Processes;
using Fuse.Operations;
using Fuse.Operations.Test;
using Fuse.Protocol.Testing;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Unit;

public class TestTimeoutTests
{
    [Fact]
    public void A_timed_out_run_names_its_project_framework_and_timeout()
    {
        var result = new ProcessResult(-1, "") { TimedOut = true };

        var answer = TestOperation.WithoutResults(result, FixtureRepo.CheckoutRoot, "the test run of Hang.Tests", "ran 1 test(s)", 1.0, timedOutFramework: "net10.0");

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        Assert.Equal("fuse: the test run of Hang.Tests (net10.0) timed out after 30 minutes without output in 1.0 s; the test process was killed", answer.Text);
    }

    [Fact]
    public void A_timed_out_run_covering_many_frameworks_names_no_framework()
    {
        var result = new ProcessResult(-1, "") { TimedOut = true };

        var answer = TestOperation.WithoutResults(result, FixtureRepo.CheckoutRoot, "the test run of Hang.Tests", "ran 1 test(s)", 1.0);

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        Assert.Equal("fuse: the test run of Hang.Tests timed out after 30 minutes without output in 1.0 s; the test process was killed", answer.Text);
    }

    [Fact]
    public void A_timed_out_run_names_a_short_timeout_in_seconds()
    {
        var result = new ProcessResult(-1, "") { TimedOut = true };

        var answer = TestOperation.WithoutResults(result, FixtureRepo.CheckoutRoot, "the test run of Hang.Tests", "ran 1 test(s)", 12.0, timedOutFramework: "net10.0", inactivityTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        Assert.Equal("fuse: the test run of Hang.Tests (net10.0) timed out after 10 seconds without output in 12.0 s; the test process was killed", answer.Text);
    }

    [Fact]
    public void A_timed_out_run_prints_the_end_of_its_output_first()
    {
        var result = new ProcessResult(-1, "first\nsecond\n") { TimedOut = true };

        var answer = TestOperation.WithoutResults(result, FixtureRepo.CheckoutRoot, "the test run of Hang.Tests", "ran 1 test(s)", 1.0, timedOutFramework: "net10.0");

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        Assert.StartsWith("first\nsecond\nfuse: the test run of Hang.Tests (net10.0) timed out", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timed_out_run_is_never_answered_as_passed()
    {
        var result = new ProcessResult(0, "") { TimedOut = true };

        var answer = TestOperation.WithoutResults(result, FixtureRepo.CheckoutRoot, "the test run of Hang.Tests", "ran 1 test(s)", 1.0, timedOutFramework: "net10.0");

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
    }

    [Fact]
    public void A_shadow_run_names_its_variant_framework()
    {
        var assembly = Path.Combine("shadow", "Lib.Tests-net9.0", "g1", "Lib.Tests.dll");
        var run = new TestRun("Lib.Tests.csproj", "Lib.Tests", new TestRunMode.Shadow(assembly), null, false, null, [], []);

        Assert.Equal("net9.0", TestOperation.TargetFrameworkOf(run));
    }

    [Fact]
    public void A_build_run_names_the_framework_its_targets_agree_on()
    {
        var run = new TestRun("Tests.csproj", "Tests", new TestRunMode.Build(), null, true, null, [], [TargetIn("net10.0")]);

        Assert.Equal("net10.0", TestOperation.TargetFrameworkOf(run));
    }

    [Fact]
    public void A_build_run_covering_two_frameworks_names_none()
    {
        var run = new TestRun("Tests.csproj", "Tests", new TestRunMode.Build(), null, true, null, [], [TargetIn("net9.0"), TargetIn("net10.0")]);

        Assert.Null(TestOperation.TargetFrameworkOf(run));
    }

    [Fact]
    public void A_build_run_with_no_targets_names_none()
    {
        var run = new TestRun("Tests.csproj", "Tests", new TestRunMode.Build(), null, false, null, [], []);

        Assert.Null(TestOperation.TargetFrameworkOf(run));
    }

    [Theory]
    [InlineData("net9.0", "net9.0")]
    [InlineData("debug_net9.0", "net9.0")]
    [InlineData("release_net8.0", "net8.0")]
    public void A_target_path_names_its_output_framework(string folder, string expected)
    {
        Assert.Equal(expected, TestOperation.TargetFrameworkOfPath(TargetIn(folder)));
    }

    [Fact]
    public void A_target_path_outside_an_output_folder_names_none()
    {
        Assert.Null(TestOperation.TargetFrameworkOfPath(Path.Combine("shadow", "Lib.Tests-net9.0", "g1", "Lib.Tests.dll")));
    }

    [Fact]
    public void Agreed_targets_name_their_framework()
    {
        Assert.Equal("net9.0", TestOperation.AgreedFramework([TargetIn("net9.0"), TargetIn("debug_net9.0")]));
    }

    [Fact]
    public void Disagreeing_targets_name_none()
    {
        Assert.Null(TestOperation.AgreedFramework([TargetIn("net9.0"), TargetIn("net10.0")]));
        Assert.Null(TestOperation.AgreedFramework([]));
    }

    [Theory]
    [InlineData(new[] { "-f", "net9.0" }, "net9.0")]
    [InlineData(new[] { "--framework", "net8.0" }, "net8.0")]
    [InlineData(new[] { "--framework=net8.0" }, "net8.0")]
    [InlineData(new[] { "Tests.csproj", "--no-restore" }, null)]
    [InlineData(new[] { "-f" }, null)]
    public void Arguments_name_their_framework(string[] arguments, string? expected)
    {
        Assert.Equal(expected, TestOperation.FrameworkFromArguments(arguments));
    }

    private static string TargetIn(string folder) =>
        Path.Combine("Tests", "bin", "Debug", folder, "Tests.dll");
}
