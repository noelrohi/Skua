using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// Turn-ins and the game server's answer to them (its <c>ccqr</c> packet), and the repeating quests' completion, against the fake game's quests:
/// Slime Time, accepted with its requirements unmet; Chest Hoarder, ready to turn in; and Weekly Slimes, a weekly quest.
/// </summary>
public class QuestTurnInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_turn_in_the_game_server_refuses_is_a_quest_rejected_event_with_its_reason_a_Script_log_line_and_shows_in_quests()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("turnin-refuse 1002 Weekly Quests are only available once per week.");

        // As a Script turns it in.
        await session.Connection.EvalAsync("Bot.Quests.Complete(1002)", cancellationToken: Ct);
        LogEntryDto rejected = await session.Connection.WaitForEventAsync(EventTypes.QuestRejected);
        List<LogEntryDto> lines = await session.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text!.Contains("1002"));
        QuestDto refused = await QuestAsync(session, 1002);
        QuestCompleteResult again = await session.Connection.QuestCompleteAsync(1002, cancellationToken: Ct);
        LogEntryDto completed = await session.Connection.WaitForEventAsync(EventTypes.QuestCompleted);
        // Core reads the quest tree at most every 100 ms.
        await Task.Delay(150, Ct);
        QuestDto turnedIn = await QuestAsync(session, 1002);

        // The refusal carries no quest ID, as the game's own handler reads none; it is the quest last sent for turn-in.
        JsonElement data = rejected.Data!.Value;
        Assert.Equal((1002, "Chest Hoarder", "Weekly Quests are only available once per week."),
            (data.GetProperty("id").GetInt32(), data.GetProperty("name").GetString(), data.GetProperty("reason").GetString()));
        Assert.Equal("Quest 1002 'Chest Hoarder' wasn't turned in: Weekly Quests are only available once per week.", Assert.Single(lines).Text);
        Assert.Equal("Weekly Quests are only available once per week.", refused.LastRejection!.Reason);
        // A turn-in the server accepts ends the quest and forgets its refusal.
        Assert.Equal(new QuestCompleteResult(1002, "Chest Hoarder", Completed: true, Reason: null), again);
        Assert.Equal((1002, "Chest Hoarder"), (completed.Data!.Value.GetProperty("id").GetInt32(), completed.Data!.Value.GetProperty("name").GetString()));
        Assert.Equal(QuestStatus.NotAccepted, turnedIn.Status);
        Assert.Null(turnedIn.LastRejection);
    }

    [Fact]
    public async Task Quest_complete_answers_with_the_game_servers_reply_and_fails_on_time_when_it_never_answers()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        QuestCompleteResult notReady = await session.Connection.QuestCompleteAsync(1001, cancellationToken: Ct);
        LogEntryDto rejected = await session.Connection.WaitForEventAsync(EventTypes.QuestRejected);
        // Not accepted, so the fake server ignores it.
        ControlException unanswered = await Assert.ThrowsAsync<ControlException>(() => session.Connection.QuestCompleteAsync(1005, timeoutSec: 1, cancellationToken: Ct));
        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        ControlException running = await Assert.ThrowsAsync<ControlException>(() => session.Connection.QuestCompleteAsync(1002, cancellationToken: Ct));

        Assert.Equal(new QuestCompleteResult(1001, "Slime Time", Completed: false, Reason: null), notReady);
        Assert.Equal(JsonValueKind.Null, rejected.Data!.Value.GetProperty("reason").ValueKind);
        Assert.Contains("tryQuestComplete 1001 -1", await session.GameHost.CallsAsync());
        Assert.Equal(ErrorCode.Timeout, unanswered.Code);
        Assert.Contains("quest 1005", unanswered.Message);
        Assert.Equal(ErrorCode.ScriptRunning, running.Code);
    }

    [Fact]
    public async Task A_weekly_quest_done_this_week_reads_as_done()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        QuestDto before = await QuestAsync(session, 1005);
        bool scriptBefore = (await session.Connection.EvalAsync("Bot.Quests.IsDailyComplete(1005)", cancellationToken: Ct)).Value!.Value.GetBoolean();
        // The game server's setAchievement, as it sends it once the weekly is turned in.
        await session.GameHost.DoAsync("achievement iw0 3 1");
        QuestDto after = await QuestAsync(session, 1005);
        bool scriptAfter = (await session.Connection.EvalAsync("Bot.Quests.IsDailyComplete(1005)", cancellationToken: Ct)).Value!.Value.GetBoolean();
        QuestDto once = await QuestAsync(session, 1001);

        Assert.Equal((QuestRepeat.Weekly, (bool?)false), (before.Repeat, before.RepeatDone));
        Assert.False(scriptBefore);
        Assert.Equal((QuestRepeat.Weekly, (bool?)true), (after.Repeat, after.RepeatDone));
        Assert.True(scriptAfter);
        Assert.Equal(((QuestRepeat?)null, (bool?)null), (once.Repeat, once.RepeatDone));
    }

    [Fact]
    public async Task The_CLI_turns_a_quest_in_and_quests_shows_the_refusal_and_the_weeklys_completion()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        FakeGameHost gameHost = new(sandbox);
        Dictionary<string, string> environment = GameFixture.Environment(gameHost.Game(keychain, GameFixture.Servers), api, keychain);

        await sandbox.RunCliAsync(environment, "login", "Galanoth");
        ProcessResult refused = await sandbox.RunCliAsync(environment, "quests", "complete", "1001");
        ProcessResult quests = await sandbox.RunCliAsync(environment, "quests");
        ProcessResult json = await sandbox.RunCliAsync(environment, "quests", "active", "--json");
        ProcessResult completed = await sandbox.RunCliAsync(environment, "quests", "complete", "1002", "--json");

        Assert.Equal((1, "The game server refused to turn in quest 1001 Slime Time and gave no reason."), (refused.ExitCode, refused.Stdout.Trim()));
        Assert.Contains("1001 Slime Time: inProgress\n    last turn-in refused, with no reason\n", quests.Stdout);
        Assert.Contains("1002 Chest Hoarder: completable, member-only\n", quests.Stdout);
        Assert.Contains("1005 Weekly Slimes: notAccepted, weekly, not done this week\n", quests.Stdout);
        using (JsonDocument active = JsonDocument.Parse(json.Stdout))
        {
            JsonElement slimes = active.RootElement.GetProperty("quests")[0];
            Assert.Equal(JsonValueKind.Null, slimes.GetProperty("lastRejection").GetProperty("reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, slimes.GetProperty("repeat").ValueKind);
        }
        Assert.Equal(0, completed.ExitCode);
        using (JsonDocument result = JsonDocument.Parse(completed.Stdout))
            Assert.True(result.RootElement.GetProperty("completed").GetBoolean());
    }

    private static async Task<QuestDto> QuestAsync(GameFixture session, int id) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == id);
}
