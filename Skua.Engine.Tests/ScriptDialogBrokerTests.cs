using Skua.MacOS.Services;

namespace Skua.Engine.Tests;

/// <summary>The broker's Questions that block no thread, which the Mac App's UI thread raises, and answers from the window.</summary>
public class ScriptDialogBrokerTests
{
    private static readonly string[] YesNo = ["Yes", "No"];

    [Fact]
    public async Task AskAsync_returns_at_once_and_completes_with_the_windows_answer()
    {
        ScriptDialogBroker broker = new();
        List<(int Id, int? Choice, QuestionAnswerer By)> answers = [];
        broker.QuestionAnswered += (q, c, by) => answers.Add((q.Id, c, by));

        Task<int?> asked = broker.AskAsync("Stop?", "Stop the running Script?", YesNo, ScriptDialogBroker.OutsideRun);

        Assert.False(asked.IsCompleted);
        Question question = Assert.Single(broker.Pending());
        Assert.Equal((null, null), (question.Script, question.Run));
        Assert.True(broker.Answer(question.Id, 1, QuestionAnswerer.User));
        Assert.Equal(1, await asked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal([(question.Id, 1, QuestionAnswerer.User)], answers);
        // The first answer won.
        Assert.False(broker.Answer(question.Id, 0, QuestionAnswerer.User));
        Assert.Equal(AnswerOutcome.NotPending, broker.Answer(question.Id, "Yes").Outcome);
        Assert.Empty(broker.Pending());
    }

    [Fact]
    public async Task AskAsync_gets_the_fallback_at_its_timeout_or_when_cancelled()
    {
        ScriptDialogBroker broker = new();
        List<QuestionAnswerer> answeredBy = [];
        broker.QuestionAnswered += (_, _, by) => answeredBy.Add(by);
        using CancellationTokenSource exiting = new();

        Task<int?> timedOut = broker.AskAsync("Late", "Nobody answers.", YesNo, ScriptDialogBroker.OutsideRun with { Timeout = TimeSpan.FromMilliseconds(100) });
        Task<int?> cancelled = broker.AskAsync("Quit", "The app quits.", YesNo, ScriptDialogBroker.OutsideRun, exiting.Token);
        Assert.Null(await timedOut.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        exiting.Cancel();
        Assert.Null(await cancelled.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal([QuestionAnswerer.Timeout, QuestionAnswerer.Fallback], answeredBy);
        Assert.Empty(broker.Pending());
    }

    [Fact]
    public void A_window_answer_needs_a_choice_the_Question_offers()
    {
        ScriptDialogBroker broker = new();
        _ = broker.AskAsync("Pick", "Pick one.", YesNo, ScriptDialogBroker.OutsideRun);
        int id = Assert.Single(broker.Pending()).Id;

        Assert.False(broker.Answer(id, 2, QuestionAnswerer.User));
        Assert.False(broker.Answer(id, -1, QuestionAnswerer.User));
        Assert.False(broker.Answer(id + 1, 0, QuestionAnswerer.User));
        Assert.Single(broker.Pending());
        broker.ResolveRun(0);
        Assert.True(broker.Answer(id, 0, QuestionAnswerer.User));
    }
}
