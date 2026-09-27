using System.Diagnostics;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Script Dialogs: Notices, Questions, <c>dialogs</c> and <c>dialog_answer</c>, against the fake Game Host.</summary>
public class DialogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Notice_never_waits_and_is_emitted_with_its_full_text_up_to_64_KB()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Notice.cs", TestScripts.Main("""
            bool? ok = bot.ShowMessageBox(new string('a', 20 * 1024), "Result");
            bot.ShowMessageBox(new string('b', 70 * 1024), "Too long", false);
            bot.Log($"returned {(ok is null ? "null" : ok.ToString())}");
            """));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Notice.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptWaitReason.Ended, wait.Reason);
        Assert.Equal(ScriptOutcome.Completed, wait.Status.LastRun!.Outcome);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "returned null");
        List<LogEntryDto> notices = await DialogEvents.OfTypeAsync(game.Connection, EventTypes.NoticeShown);
        Assert.Equal(2, notices.Count);
        Assert.Equal(("Result", new string('a', 20 * 1024), "Script Thread", "Tests/Notice.cs"),
            (Get(notices[0], "caption"), Get(notices[0], "text"), Get(notices[0], "thread"), Get(notices[0], "script")));
        Assert.False(notices[0].Truncated);
        Assert.Equal(new string('b', 64 * 1024), Get(notices[1], "text"));
        Assert.True(notices[1].Truncated);
        Assert.All(notices, n => Assert.Equal(start.Run, n.Run));
        Assert.Empty(await DialogEvents.OfTypeAsync(game.Connection, EventTypes.QuestionRaised));
        Assert.Empty((await game.Connection.DialogsAsync(Ct)).Questions);
    }

    [Fact]
    public async Task A_Notice_raised_on_the_timer_thread_doesnt_stall_handlers()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/HandlerNotice.cs", TestScripts.Main("""
            int ticks = 0;
            bot.Handlers.RegisterHandler(1, b =>
            {
                b.ShowMessageBox($"tick {Interlocked.Increment(ref ticks)}", "Handler");
                return true;
            });
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref ticks) < 5 && waited.Elapsed.TotalSeconds < 20)
                Thread.Sleep(20);
            bot.Log($"ticks {(Volatile.Read(ref ticks) >= 5 ? "reached" : "stalled")}");
            """));

        await game.Connection.ScriptStartAsync("Tests/HandlerNotice.cs", cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Single(await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text?.StartsWith("ticks ", StringComparison.Ordinal) == true), e => e.Text == "ticks reached");
        List<LogEntryDto> notices = await DialogEvents.OfTypeAsync(game.Connection, EventTypes.NoticeShown);
        Assert.True(notices.Count >= 5);
        Assert.All(notices, n => Assert.Equal(("Handler", "ScriptInterface"), (Get(n, "caption"), Get(n, "thread"))));
    }

    [Fact]
    public async Task A_Question_in_ask_mode_waits_until_dialog_answer_answers_it_and_a_second_answer_is_DialogNotPending()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Ask.cs", TestScripts.Main("""
            bool? answer = bot.ShowMessageBox("Buy the Class for 500 AC?", "Confirm", true);
            bot.Log($"answer {(answer is null ? "null" : answer.ToString())}");
            """));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Ask.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);
        ScriptWaitResult again = await game.Connection.ScriptWaitAsync(60, Ct);
        StatusDto status = await game.Connection.StatusAsync(Ct);
        QuestionDto question = Assert.Single((await game.Connection.DialogsAsync(Ct)).Questions);
        DialogAnswerResult answer = await game.Connection.DialogAnswerAsync(question.Id, "yes", Ct);
        ScriptWaitResult ended = await game.Connection.ScriptWaitAsync(60, Ct);
        ControlException duplicate = await Assert.ThrowsAsync<ControlException>(() => game.Connection.DialogAnswerAsync(question.Id, "No", Ct));

        Assert.Equal((ScriptWaitReason.Question, ScriptState.Running), (wait.Reason, wait.Status.State));
        Assert.Equal(ScriptWaitReason.Question, again.Reason);
        Assert.Equal(Json(question), Json(Assert.Single(status.PendingDialogs)));
        Assert.Equal(("Confirm", "Buy the Class for 500 AC?", "Script Thread", "Tests/Ask.cs"), (question.Caption, question.Text, question.Thread, question.Script));
        Assert.Equal(["Yes", "No"], question.Choices);
        Assert.Equal(TimeSpan.FromSeconds(120), question.ExpiresAt - question.RaisedAt);
        Assert.Equal(new DialogAnswerResult(question.Id, "Yes"), answer);
        Assert.Equal(ScriptWaitReason.Ended, ended.Reason);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "answer True");
        Assert.Equal(ErrorCode.DialogNotPending, duplicate.Code);
        Assert.Empty((await game.Connection.DialogsAsync(Ct)).Questions);
        Assert.Empty((await game.Connection.StatusAsync(Ct)).PendingDialogs);

        List<LogEntryDto> events = await DialogEvents.AllAsync(game.Connection);
        Assert.Equal([EventTypes.QuestionRaised, EventTypes.QuestionAnswered], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(start.Run, e.Run));
        Assert.Equal((question.Id, "Buy the Class for 500 AC?"), (events[0].Data!.Value.GetProperty("id").GetInt32(), Get(events[0], "text")));
        Assert.Equal((question.Id, "Yes", "agent"), (events[1].Data!.Value.GetProperty("id").GetInt32(), Get(events[1], "choice"), Get(events[1], "answeredBy")));
    }

    [Fact]
    public async Task A_Question_with_named_buttons_returns_the_chosen_button_and_refuses_a_choice_it_doesnt_offer()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Buttons.cs", TestScripts.Main("""
            var picked = bot.ShowMessageBox("Which class?", "Class", "Warrior", "Mage", "Rogue");
            bot.Log($"picked {picked.Text} {picked.Value}");
            """));

        await game.Connection.ScriptStartAsync("Tests/Buttons.cs", cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);
        QuestionDto question = Assert.Single((await game.Connection.DialogsAsync(Ct)).Questions);
        ControlException unknown = await Assert.ThrowsAsync<ControlException>(() => game.Connection.DialogAnswerAsync(question.Id, "Paladin", Ct));
        QuestionDto stillPending = Assert.Single((await game.Connection.DialogsAsync(Ct)).Questions);
        ControlException notAQuestion = await Assert.ThrowsAsync<ControlException>(() => game.Connection.DialogAnswerAsync(question.Id + 100, "Mage", Ct));
        await game.Connection.DialogAnswerAsync(question.Id, "MAGE", Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(["Warrior", "Mage", "Rogue"], question.Choices);
        Assert.Equal(ErrorCode.InvalidArgument, unknown.Code);
        Assert.Contains("Warrior, Mage, Rogue", unknown.Message);
        Assert.Equal(Json(question), Json(stillPending));
        Assert.Equal(ErrorCode.DialogNotPending, notAQuestion.Code);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "picked Mage 1");
    }

    [Fact]
    public async Task A_Question_nobody_answers_gets_the_fallback_at_the_timeout_never_the_first_button()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Unanswered.cs", TestScripts.Main("""
            bool? yes = bot.ShowMessageBox("Continue?", "Confirm", true);
            var picked = bot.ShowMessageBox("Which class?", "Class", "Warrior", "Mage");
            bot.Log($"got {(yes is null ? "null" : yes.ToString())} {picked.Text} {picked.Value}");
            """));

        Stopwatch waited = Stopwatch.StartNew();
        await game.Connection.ScriptStartAsync("Tests/Unanswered.cs", dialogTimeoutSec: 1, cancellationToken: Ct);
        ScriptWaitResult first = await game.Connection.ScriptWaitAsync(60, Ct);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "got null Cancel -1");

        Assert.Equal(ScriptWaitReason.Question, first.Reason);
        Assert.InRange(waited.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        List<LogEntryDto> raised = await DialogEvents.OfTypeAsync(game.Connection, EventTypes.QuestionRaised);
        Assert.Equal(2, raised.Count);
        Assert.All(raised, e => Assert.Equal(TimeSpan.FromSeconds(1), DateTimeOffset.Parse(Get(e, "expiresAt")!) - DateTimeOffset.Parse(Get(e, "raisedAt")!)));
        List<LogEntryDto> answered = await DialogEvents.OfTypeAsync(game.Connection, EventTypes.QuestionAnswered);
        Assert.Equal(2, answered.Count);
        Assert.All(answered, e => Assert.Equal(("timeout", JsonValueKind.Null), (Get(e, "answeredBy"), e.Data!.Value.GetProperty("choice").ValueKind)));
    }

    [Fact]
    public async Task In_cancel_mode_a_Question_gets_the_fallback_at_once_and_script_wait_doesnt_stop_for_it()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Cancelled.cs", TestScripts.Main("""
            bool? yes = bot.ShowMessageBox("Continue?", "Confirm", true);
            var picked = bot.ShowMessageBox("Which class?", "Class", "Warrior", "Mage");
            bot.Log($"got {(yes is null ? "null" : yes.ToString())} {picked.Text} {picked.Value}");
            """));

        await game.Connection.ScriptStartAsync("Tests/Cancelled.cs", dialogs: DialogMode.Cancel, cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptWaitReason.Ended, wait.Reason);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "got null Cancel -1");
        List<LogEntryDto> events = await DialogEvents.AllAsync(game.Connection);
        Assert.Equal([EventTypes.QuestionRaised, EventTypes.QuestionAnswered, EventTypes.QuestionRaised, EventTypes.QuestionAnswered], events.Select(e => e.Type));
        Assert.Equal(Get(events[0], "raisedAt"), Get(events[0], "expiresAt"));
        Assert.All(events.Where(e => e.Type == EventTypes.QuestionAnswered), e => Assert.Equal("fallback", Get(e, "answeredBy")));
    }

    [Fact]
    public async Task Script_stop_answers_a_pending_Question_with_the_fallback_before_stopping_the_Script()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        string seen = Path.Combine(sandbox.SkuaDir, "answer");
        // Not bot.Log: it throws once the stop has cancelled the Script, which may be before the Script records the answer.
        TestScripts.Write(sandbox, "Tests/AskThenLoop.cs", TestScripts.Main($$"""
            bool? answer = bot.ShowMessageBox("Continue?", "Confirm", true);
            System.IO.File.WriteAllText("{{seen}}", answer is null ? "null" : answer.ToString());
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));

        await game.Connection.ScriptStartAsync("Tests/AskThenLoop.cs", cancellationToken: Ct);
        await game.Connection.ScriptWaitAsync(60, Ct);
        QuestionDto question = Assert.Single((await game.Connection.DialogsAsync(Ct)).Questions);
        Stopwatch stopping = Stopwatch.StartNew();
        ScriptStopResult stop = await game.Connection.ScriptStopAsync(Ct);

        Assert.True(stop.Ended);
        Assert.Equal(ScriptOutcome.Stopped, stop.Status.LastRun!.Outcome);
        // Core interrupts a thread that ignores its stop only after 5 s.
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(5), $"The stop took {stopping.Elapsed}.");
        Assert.Equal("null", await File.ReadAllTextAsync(seen, Ct));
        LogEntryDto answered = Assert.Single(await DialogEvents.OfTypeAsync(game.Connection, EventTypes.QuestionAnswered));
        Assert.Equal((question.Id, "fallback"), (answered.Data!.Value.GetProperty("id").GetInt32(), Get(answered, "answeredBy")));
        Assert.Empty((await game.Connection.DialogsAsync(Ct)).Questions);
    }

    [Fact]
    public async Task ShowDialog_and_the_file_dialogs_are_never_surfaced()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        EvalResult eval = await game.Connection.EvalAsync("""
            var dialogs = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService(typeof(IDialogService)) as IDialogService;
            var files = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService(typeof(IFileDialogService)) as IFileDialogService;
            return $"{dialogs.ShowDialog(new object()) is null} {files.OpenFile() is null} {files.Save() is null}";
            """, cancellationToken: Ct);

        Assert.Null(eval.Error);
        Assert.Equal("True True True", eval.Value!.Value.GetString());
        Assert.Empty(await DialogEvents.AllAsync(game.Connection));
        Assert.Empty((await game.Connection.DialogsAsync(Ct)).Questions);
        await game.Connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text?.Contains("isn't surfaced headless", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_Questions_text_is_redacted_in_dialogs_and_its_events()
    {
        const string secret = "hunter2-SECRET";
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain);
        environment["SKUA_REDACT"] = secret;
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(environment);
        using (connection)
        {
            TestScripts.Write(sandbox, "Tests/Leaky.cs", TestScripts.Main($$"""
                bot.ShowMessageBox("password {{secret}}", "Leak {{secret}}");
                bot.ShowMessageBox("password {{secret}}", "Leak {{secret}}", true);
                """));

            await connection.ScriptStartAsync("Tests/Leaky.cs", cancellationToken: Ct);
            await connection.ScriptWaitAsync(60, Ct);
            QuestionDto question = Assert.Single((await connection.DialogsAsync(Ct)).Questions);
            await connection.ScriptStopAsync(Ct);

            Assert.Equal(("Leak [redacted]", "password [redacted]"), (question.Caption, question.Text));
            Assert.DoesNotContain("hunt", JsonSerializer.Serialize(await DialogEvents.AllAsync(connection), ControlJson.Options));
        }
    }

    private static string? Get(LogEntryDto entry, string property) => ScriptEvents.Get(entry, property);

    private static string Json(QuestionDto question) => JsonSerializer.Serialize(question, ControlJson.Options);
}

/// <summary>Reads the <c>notice.*</c> and <c>question.*</c> events.</summary>
public static class DialogEvents
{
    public static async Task<List<LogEntryDto>> AllAsync(EngineConnection connection) =>
        (await ScriptEvents.AllOfAsync(connection)).Where(e => e.Type!.StartsWith("notice.", StringComparison.Ordinal) || e.Type.StartsWith("question.", StringComparison.Ordinal)).ToList();

    public static async Task<List<LogEntryDto>> OfTypeAsync(EngineConnection connection, string type) =>
        (await AllAsync(connection)).Where(e => e.Type == type).ToList();
}
