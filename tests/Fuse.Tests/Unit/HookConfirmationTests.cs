using Fuse.Failures;
using Fuse.Harnesses;
using Fuse.Hooks;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

/// <summary>
///     What a post-edit hook answers from scripted checks. A harness that runs the hook in the background confirms an
///     answer with errors against the current tree before returning it: the second answer is delivered when it reports,
///     and nothing is answered when the tree no longer reports it, when the confirmation cannot answer, or when no wait
///     is left. A hook the harness runs inline answers from the first check.
/// </summary>
public class HookConfirmationTests
{
    private const string First = "fuse: 1 error(s) introduced in 1 file(s) (Lib)";
    private const string Second = "fuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests)";
    private const string Clean = "fuse: no errors introduced (1 file(s) checked)";

    private static Harness Named(string name) =>
        SupportedHarnesses.Find(name) ?? throw new InvalidOperationException($"no harness {name}");

    [Fact]
    public async Task Broken_then_clean_answers_nothing_and_checks_twice()
    {
        var scripted = new ScriptedAnswers(Broken(First), CleanAnswer());
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(HookAnswer.None, answer);
        Assert.Equal(2, scripted.Calls);
        Assert.Equal($"claude post-edit dropped: {First} (a check of the current tree did not report it)", Assert.Single(logged));
    }

    [Fact]
    public async Task Broken_then_broken_delivers_the_second_answer()
    {
        var scripted = new ScriptedAnswers(Broken(First), Broken(Second));
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Named("claude").ReportAfterEdit(Second), answer);
        Assert.Equal(2, scripted.Calls);
        Assert.Equal($"claude post-edit delivered: {Second}", Assert.Single(logged));
    }

    [Fact]
    public async Task Broken_then_unanswered_answers_nothing()
    {
        var timeout = new EngineResponse.Unanswered(ErrorCode.Timeout, "the engine did not answer in time");
        var scripted = new ScriptedAnswers(Broken(First), (OperationResult.Unanswered(timeout, "check result"), timeout));
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(HookAnswer.None, answer);
        Assert.Equal(2, scripted.Calls);
        Assert.Equal($"claude post-edit dropped: {First} (a check of the current tree did not report it)", Assert.Single(logged));
    }

    [Fact]
    public async Task Clean_first_checks_once_and_answers_nothing()
    {
        var scripted = new ScriptedAnswers(CleanAnswer());
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(HookAnswer.None, answer);
        Assert.Equal(1, scripted.Calls);
        Assert.Empty(logged);
    }

    [Fact]
    public async Task Restore_needed_then_clean_answers_nothing()
    {
        var scripted = new ScriptedAnswers(RestoreNeeded(), CleanAnswer());
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(HookAnswer.None, answer);
        Assert.Equal(2, scripted.Calls);
        Assert.Single(logged, l => l.StartsWith("claude post-edit dropped: fuse: restore needed: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_needed_then_restore_needed_delivers_the_second_answer()
    {
        var first = RestoreNeeded("Lib/Lib.csproj");
        var second = RestoreNeeded("App/App.csproj");
        var scripted = new ScriptedAnswers(first, second);
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Named("claude").ReportAfterEdit(second.Result.Text), answer);
        Assert.Equal(2, scripted.Calls);
        Assert.Equal($"claude post-edit delivered: {second.Result.Text}", Assert.Single(logged));
    }

    [Fact]
    public async Task Inline_harness_delivers_the_first_answer_without_confirming()
    {
        var scripted = new ScriptedAnswers(Broken(First));
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("cursor"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Named("cursor").ReportAfterEdit(First), answer);
        Assert.Equal(1, scripted.Calls);
        Assert.Empty(logged);
    }

    [Fact]
    public async Task No_remaining_wait_drops_without_confirming()
    {
        var scripted = new ScriptedAnswers(Broken(First));
        var logged = new List<string>();

        var answer = await HookCommand.PostEditAsync(Named("claude"), scripted.Check, () => TimeSpan.Zero, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal(HookAnswer.None, answer);
        Assert.Equal(1, scripted.Calls);
        Assert.Equal($"claude post-edit dropped: {First} (a check of the current tree did not report it)", Assert.Single(logged));
    }

    [Fact]
    public async Task The_logged_summary_is_the_answers_last_line()
    {
        var multiline = "Lib/Calc.cs(7,13): error CS0103: The name 'undefinedValue' does not exist\n" + First;
        var scripted = new ScriptedAnswers(Broken(multiline), CleanAnswer());
        var logged = new List<string>();

        await HookCommand.PostEditAsync(Named("claude"), scripted.Check, Plenty, logged.Add, TestContext.Current.CancellationToken);

        Assert.Equal($"claude post-edit dropped: {First} (a check of the current tree did not report it)", Assert.Single(logged));
    }

    private static TimeSpan Plenty() => TimeSpan.FromSeconds(30);

    // The confirmation reads only whether an answer reports and its text, so a scripted answer pairs its result with an
    // acknowledged response except where the response itself decides: a missing restore and a timeout.
    private static (OperationResult Result, EngineResponse Response) Broken(string text) =>
        (new OperationResult(Outcome.ProblemsFound, text), new EngineResponse.Acknowledged());

    private static (OperationResult Result, EngineResponse Response) CleanAnswer() =>
        (new OperationResult(Outcome.Clean, Clean), new EngineResponse.Acknowledged());

    private static (OperationResult Result, EngineResponse Response) RestoreNeeded(string project = "Lib/Lib.csproj")
    {
        var response = new EngineResponse.Unanswered(ErrorCode.RestoreNeeded, $"restore needed: run `dotnet restore {project}` ({project} not restored)");
        return (OperationResult.Unanswered(response, "check result"), response);
    }

    /// <summary>One queued answer per check, counting how many checks ran.</summary>
    private sealed class ScriptedAnswers(params (OperationResult Result, EngineResponse Response)[] answers)
    {
        private readonly Queue<(OperationResult Result, EngineResponse Response)> _answers = new(answers);

        public int Calls { get; private set; }

        public Task<(OperationResult Result, EngineResponse Response)> Check(CancellationToken _)
        {
            Calls++;
            return Task.FromResult(_answers.Dequeue());
        }
    }
}
