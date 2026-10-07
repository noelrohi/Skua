using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>How long the quests' requirements go without a rise, and <c>quest.stalled</c>, against the fake game's Slime Time, its Slime Samples and its one Slime Crown.</summary>
public class QuestProgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Fast = new()
    {
        ["SKUA_QUEST_SAMPLE_MS"] = "100",
        ["SKUA_QUEST_STALL_SEC"] = "2",
    };

    [Fact]
    public async Task A_requirement_is_idle_until_its_count_rises_and_a_fall_is_no_rise()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await Task.Delay(1500, Ct);
        QuestRequirementDto waited = await SlimesAsync(session);
        await session.GameHost.DoAsync("slime-samples 4");
        QuestRequirementDto rose = await SlimesAsync(session);
        await session.GameHost.DoAsync("slime-samples 1");
        await Task.Delay(500, Ct);
        QuestRequirementDto fell = await SlimesAsync(session);

        Assert.Equal(3, waited.Have);
        Assert.InRange(waited.IdleSec!.Value, 1.4, 30);
        Assert.Null(waited.GainPerHour);
        Assert.Equal(4, rose.Have);
        Assert.InRange(rose.IdleSec!.Value, 0, 1);
        Assert.Equal(1, fell.Have);
        Assert.InRange(fell.IdleSec!.Value, 0.5, rose.IdleSec.Value + 30);
    }

    [Fact]
    public async Task A_runs_quests_stall_once_without_a_rise_and_status_says_for_how_long()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        ScriptStartResult start = await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        ScriptRunDto run = (await session.Connection.StatusAsync(Ct)).Script.Run!;
        // The crown is a 1/1 drop: the rise that meets it is progress too, though Slime Time still needs Slime Samples.
        await session.GameHost.DoAsync("slime-crowns 1");
        // status reads what the last sample saw, so the rise shows within a sample.
        ScriptRunDto progressed = await WaitForAsync(async () => (await session.Connection.ScriptStatusAsync(Ct)).Run!, r => r.QuestIdleSec < 1);
        LogEntryDto again = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled, e => e.Seq > stalled.Seq);

        JsonElement data = stalled.Data!.Value;
        Assert.Equal(start.Run, data.GetProperty("run").GetInt32());
        Assert.Equal("Tests/Loop.cs", data.GetProperty("script").GetString());
        Assert.True(data.GetProperty("idleSec").GetDouble() >= 2);
        // Slime Time needs 5 Slime Samples and a Slime Crown; Chest Hoarder is completable and Not Yet isn't accepted, so neither is stalled.
        JsonElement quest = Assert.Single(data.GetProperty("quests").EnumerateArray().ToList());
        Assert.Equal((1001, "Slime Time"), (quest.GetProperty("id").GetInt32(), quest.GetProperty("name").GetString()));
        Assert.Equal([("Slime Sample", 3, 5), ("Slime Crown", 0, 1)], Unmet(quest));
        Assert.InRange(run.QuestIdleSec!.Value, 2, run.ElapsedSec);
        Assert.InRange(progressed.QuestIdleSec!.Value, 0, 1);
        // A rise re-arms it, so the next stall is recorded too, with only what is left.
        Assert.Equal([("Slime Sample", 3, 5)], Unmet(again.Data!.Value.GetProperty("quests")[0]));
    }

    [Fact]
    public async Task A_stall_accepts_the_quests_it_lists_again()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        string[] calls = await session.GameHost.WaitForCallAsync("acceptQuest 1001");
        // The Engine log says what the accept left: the fake game still has Slime Time in progress.
        await session.Connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("Accepted the stalled quests again: 1001 in progress.", StringComparison.Ordinal));

        Assert.Equal([1001], Reaccepted(stalled));
        // Only Slime Time is listed: Chest Hoarder is completable and Not Yet isn't accepted, so neither is accepted again.
        Assert.Equal(["acceptQuest 1001"], calls.Where(c => c.StartsWith("acceptQuest ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_stall_accepts_nothing_again_when_turned_off()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string>(Fast) { ["SKUA_QUEST_STALL_REACCEPT"] = "0" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        // A poll that sees the rise starts only once the stall's poll, which would have sent the accepts, has ended.
        await session.GameHost.DoAsync("slime-crowns 1");
        ScriptRunDto progressed = await WaitForAsync(async () => (await session.Connection.ScriptStatusAsync(Ct)).Run!, r => r.QuestIdleSec < 1);

        Assert.InRange(progressed.QuestIdleSec!.Value, 0, 1);
        Assert.Empty(Reaccepted(stalled));
        Assert.DoesNotContain(await session.GameHost.CallsAsync(), c => c.StartsWith("acceptQuest ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quest_the_run_left_behind_neither_stalls_nor_is_accepted_again_while_another_progresses()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        // An earlier step's quest, which the run leaves accepted: Not Yet's requirements never rise.
        await session.Connection.EvalAsync("Bot.Quests.Accept(1003)", cancellationToken: Ct);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        // Slime Time progresses for twice the stall time, then stalls.
        for (int samples = 4; samples <= 12; samples++)
        {
            await session.GameHost.DoAsync($"slime-samples {samples}");
            await Task.Delay(500, Ct);
        }
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        await session.Connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("Accepted the stalled quests again: 1001 in progress.", StringComparison.Ordinal));

        Assert.Single(await AllAsync(session, LogKind.Events), e => e.Type == EventTypes.QuestStalled);
        Assert.Equal([1001], Reaccepted(stalled));
        Assert.Equal([1001], stalled.Data!.Value.GetProperty("quests").EnumerateArray().Select(q => q.GetProperty("id").GetInt32()));
        // The stall is Slime Time's: since its last rise, not since Not Yet's accept.
        Assert.InRange(stalled.Data!.Value.GetProperty("idleSec").GetDouble(), 2, 4);
        Assert.Equal(["acceptQuest 1003", "acceptQuest 1001"], (await session.GameHost.CallsAsync()).Where(c => c.StartsWith("acceptQuest ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_moment_between_a_turn_in_and_the_Scripts_accept_is_no_stall()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string>(Fast) { ["SKUA_QUEST_STALL_SEC"] = "3" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("quest-spacing 500");
        TestScripts.Write(sandbox, "Tests/Farm.cs", FarmSlimeTime(acceptAfterMs: 1400));
        await session.Connection.EvalAsync("Bot.Quests.Accept(1003)", cancellationToken: Ct);

        await session.Connection.ScriptStartAsync("Tests/Farm.cs", cancellationToken: Ct);
        // Each turn-in leaves Slime Time not accepted for 1.4 s, as CoreBots' does; Not Yet, left behind, has idled past the stall time by the last.
        for (int turnIns = 1; turnIns <= 4; turnIns++)
        {
            await Task.Delay(500, Ct);
            await session.GameHost.DoAsync("slime-samples 5");
            await session.GameHost.DoAsync("slime-crowns 1");
            await session.Connection.WaitForLogsAsync(LogKind.Events, turnIns, e => e.Type == EventTypes.QuestCompleted);
            await WaitForAsync(async () => (await session.GameHost.CallsAsync()).Count(c => c == "acceptQuest 1001"), n => n == turnIns, tries: 100);
        }
        await Task.Delay(500, Ct);

        Assert.DoesNotContain(await AllAsync(session, LogKind.Events), e => e.Type == EventTypes.QuestStalled);
        Assert.Equal((string[])["acceptQuest 1003", .. Enumerable.Repeat("acceptQuest 1001", 4)],
            (await session.GameHost.CallsAsync()).Where(c => c.StartsWith("acceptQuest ", StringComparison.Ordinal)));
        Assert.DoesNotContain(await AllAsync(session, LogKind.Game), e => e.Text!.StartsWith("Please slow down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stalls_accept_waits_for_the_players_quest_packets_to_pause()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        // Stricter than the game server's half a second.
        await session.GameHost.DoAsync("quest-spacing 1000");
        // A Script that accepts its quest every 1.5 s for 7.5 s, as the stall comes.
        TestScripts.Write(sandbox, "Tests/Accepts.cs", TestScripts.Main("""
            for (int i = 0; i < 5 && !bot.ShouldExit; i++)
            {
                bot.Quests.Accept(1001);
                Thread.Sleep(1500);
            }
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));

        await session.Connection.ScriptStartAsync("Tests/Accepts.cs", cancellationToken: Ct);
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        await session.Connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("Accepted the stalled quests again: 1001 in progress.", StringComparison.Ordinal));

        Assert.Equal([1001], Reaccepted(stalled));
        // The Script's five, then the Engine's once they have paused.
        Assert.Equal(6, (await session.GameHost.CallsAsync()).Count(c => c == "acceptQuest 1001"));
        Assert.DoesNotContain(await AllAsync(session, LogKind.Game), e => e.Text!.StartsWith("Please slow down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_accept_the_game_server_refused_is_accepted_again_within_seconds()
    {
        await using EngineSandbox sandbox = new();
        // A stall time no test waits out: only the refusal accepts it again.
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string>(Fast) { ["SKUA_QUEST_STALL_SEC"] = "600" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("quest-spacing 1000");
        // Accepts at once after the turn-in, which the game server refuses, though the game shows Slime Time accepted.
        TestScripts.Write(sandbox, "Tests/Farm.cs", FarmSlimeTime(acceptAfterMs: 0));

        await session.Connection.ScriptStartAsync("Tests/Farm.cs", cancellationToken: Ct);
        await session.GameHost.DoAsync("slime-samples 5");
        await session.GameHost.DoAsync("slime-crowns 1");
        LogEntryDto warned = (await session.Connection.WaitForLogsAsync(LogKind.Game, 1, e => e.Text!.StartsWith("Please slow down", StringComparison.Ordinal)))[0];
        LogEntryDto again = (await session.Connection.WaitForLogsAsync(LogKind.Debug, 1,
            e => e.Text!.Contains("Accepted the refused quests again: 1001 in progress.", StringComparison.Ordinal)))[0];

        Assert.Equal(2, (await session.GameHost.CallsAsync()).Count(c => c == "acceptQuest 1001"));
        // The game server took the Engine's.
        Assert.Single(await AllAsync(session, LogKind.Game), e => e.Text!.StartsWith("Please slow down", StringComparison.Ordinal));
        // A pause of 3 s in the quest packets, after the second it takes to call it refused and the next poll.
        Assert.InRange(again.Ts - warned.Ts, 2_500, 10_000);
        Assert.DoesNotContain(await AllAsync(session, LogKind.Events), e => e.Type == EventTypes.QuestStalled);
    }

    [Fact]
    public async Task A_requirement_counts_what_the_bank_holds_once_it_has_loaded_and_its_arrival_is_no_rise()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        QuestRequirementDto unloaded = await RelicsAsync(session);
        await Task.Delay(1500, Ct);
        await session.Connection.InventoryAsync(InventoryKind.Bank, Ct);
        await Task.Delay(300, Ct);
        QuestRequirementDto banked = await RelicsAsync(session);

        // Relic Keeper needs 2 Bank Relics, and the bank holds both; the game has no bank until something loads it.
        Assert.Equal((0, 0), (unloaded.Have, unloaded.InBank));
        Assert.Equal((0, 2), (banked.Have, banked.InBank));
        Assert.InRange(banked.IdleSec!.Value, 1.5, 30);
    }

    [Fact]
    public async Task A_run_counts_the_kills_credited_to_the_player_its_kills_per_minute_and_its_deaths()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        // A kill before the run isn't the run's.
        await session.GameHost.DoAsync("kill 1");
        await Task.Delay(300, Ct);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        ScriptRunDto none = (await session.Connection.StatusAsync(Ct)).Script.Run!;
        foreach (string monster in (string[])["1", "2", "1"])
            await session.GameHost.DoAsync($"kill {monster}");
        ScriptRunDto killed = await WaitForAsync(async () => (await session.Connection.StatusAsync(Ct)).Script.Run!, r => r.Kills == 3);
        LogEntryDto stalled = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled);
        await session.GameHost.DoAsync("die");
        await session.Connection.WaitForEventAsync(EventTypes.PlayerDeath);
        ScriptRunDto died = (await session.Connection.StatusAsync(Ct)).Script.Run!;

        Assert.Equal((0, 0.0, 0), (none.Kills, none.KillsPerMin, none.Deaths));
        // Within the run's first minute the rate is over a minute, so three kills are 3 a minute.
        Assert.Equal((3, 3.0), (killed.Kills, killed.KillsPerMin));
        Assert.Equal(3.0, stalled.Data!.Value.GetProperty("killsPerMin").GetDouble());
        Assert.Equal((0, 1), (killed.Deaths, died.Deaths));
    }

    [Fact]
    public async Task A_runs_goal_follows_its_CoreBots_lines_with_what_the_player_owns()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: Fast);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        string bought = Path.Combine(sandbox.SkuaDir, "bought");
        TestScripts.Write(sandbox, "Tests/Goal.cs", TestScripts.Main("""
            bot.Log("[00:00:01] (QuestProgression) Doing Quest: [1001] - \"Slime Time\"");
            bot.Log("[00:00:01] (StartBuyAllMerge) Farming to buy Slime Crown (#0/1)");
            bot.Log("[00:00:01] (BuyAllMerge) Farming Slime Sample (1/5)");
            bot.Log("[00:00:01] (HuntMonster) Killing Frogzard for item: \"Slime Sample\" 3/5");
            bot.Log("[00:00:01] (BuyAllMerge) Death - Resetting");
            while (!bot.ShouldExit && !System.IO.File.Exists(@"BOUGHT"))
                Thread.Sleep(50);
            bot.Log("[00:00:02] (BuyItem) Bought 1 Slime Crown");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """).Replace("BOUGHT", bought));
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        await session.Connection.ScriptStartAsync("Tests/Goal.cs", cancellationToken: Ct);
        ScriptGoalDto goal = (await WaitForAsync(async () => (await session.Connection.StatusAsync(Ct)).Script.Run!,
            r => r.Goal is { Resets: 1, Farm.Have: not null })).Goal!;
        File.WriteAllText(bought, "");
        ScriptGoalDto afterBuying = (await WaitForAsync(async () => (await session.Connection.StatusAsync(Ct)).Script.Run!,
            r => r.Goal is { Buy: null })).Goal!;
        await session.Connection.ScriptStopAsync(Ct);
        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        ScriptRunDto next = (await session.Connection.StatusAsync(Ct)).Script.Run!;

        // Slime Time needs Slime Samples, which the temporary inventory holds 3 of; the kill names the item it is for.
        Assert.Equal("Slime Time", goal.Quest);
        Assert.Equal(new GoalItemDto("Slime Crown", 1, 0, null), goal.Buy);
        Assert.Equal(("Slime Sample", 5, (int?)3), (goal.Farm!.Item, goal.Farm.Want, goal.Farm.Have));
        Assert.Equal("killing Frogzard for Slime Sample", goal.Now);
        Assert.NotNull(goal.LastResetAt);
        // Buying the item ends its chain below the quest.
        Assert.Equal(("Slime Time", (GoalItemDto?)null, (GoalItemDto?)null, 0), (afterBuying.Quest, afterBuying.Buy, afterBuying.Farm, afterBuying.Resets));
        // Another run has none of the last run's goal.
        Assert.Null(next.Goal);
    }

    [Fact]
    public async Task A_goals_rate_waits_out_the_burst_its_step_starts_with_then_counts_from_the_steps_line()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox,
            environment: new Dictionary<string, string>(Fast) { ["SKUA_GOAL_RATE_SEC"] = "3" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        // The player holds 3 Slime Samples as the step logs 1: a burst of 2 the moment it starts.
        TestScripts.Write(sandbox, "Tests/Farm.cs", TestScripts.Main("""
            bot.Log("[00:00:01] (BuyAllMerge) Farming Slime Sample (1/5)");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));

        DateTime started = DateTime.UtcNow;
        await session.Connection.ScriptStartAsync("Tests/Farm.cs", cancellationToken: Ct);
        GoalItemDto early = (await WaitForAsync(async () => (await session.Connection.StatusAsync(Ct)).Script.Run!,
            r => r.Goal is { Farm.Have: not null })).Goal!.Farm!;
        GoalItemDto measured = (await WaitForAsync(async () => (await session.Connection.StatusAsync(Ct)).Script.Run!,
            r => r.Goal is { Farm.PerHour: not null }, tries: 200)).Goal!.Farm!;
        double since = (DateTime.UtcNow - started).TotalHours;

        Assert.Equal((3, (double?)null), (early.Have, early.PerHour));
        // The 2 gained since the line, over at least the 3 s measured and at most the time since the run started.
        Assert.InRange(measured.PerHour!.Value, Math.Floor(2 / since), 2 * 3600 / 3.0);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, int tries = 40)
    {
        T value = await read();
        for (int i = 0; i < tries && !done(value); i++)
        {
            await Task.Delay(50, Ct);
            value = await read();
        }
        return value;
    }

    /// <summary>A Script that turns in Slime Time once it is ready and accepts it again <paramref name="acceptAfterMs"/> later.</summary>
    private static string FarmSlimeTime(int acceptAfterMs) => TestScripts.Main($$"""
        while (!bot.ShouldExit)
        {
            if (bot.Quests.CanComplete(1001))
            {
                bot.Quests.Complete(1001);
                Thread.Sleep({{acceptAfterMs}});
                bot.Quests.Accept(1001);
                // Core reads the quest tree at most every 100 ms.
                Thread.Sleep(200);
            }
            Thread.Sleep(50);
        }
        """);

    private static async Task<IReadOnlyList<LogEntryDto>> AllAsync(GameFixture session, LogKind kind) =>
        (await session.Connection.LogsAsync(kind, null, 1000, Ct)).Entries;

    private static List<int> Reaccepted(LogEntryDto stalled) =>
        stalled.Data!.Value.GetProperty("reaccepted").EnumerateArray().Select(id => id.GetInt32()).ToList();

    private static List<(string?, int, int)> Unmet(JsonElement quest) =>
        quest.GetProperty("requirements").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("have").GetInt32(), r.GetProperty("qty").GetInt32()))
            .ToList();

    private static async Task<QuestRequirementDto> RelicsAsync(GameFixture session) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == 1004).Requirements.Single();

    private static async Task<QuestRequirementDto> SlimesAsync(GameFixture session) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == 1001).Requirements.Single(r => r.ItemId == 20);
}
