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

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        T value = await read();
        for (int i = 0; i < 40 && !done(value); i++)
        {
            await Task.Delay(50, Ct);
            value = await read();
        }
        return value;
    }

    private static List<(string?, int, int)> Unmet(JsonElement quest) =>
        quest.GetProperty("requirements").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("have").GetInt32(), r.GetProperty("qty").GetInt32()))
            .ToList();

    private static async Task<QuestRequirementDto> RelicsAsync(GameFixture session) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == 1004).Requirements.Single();

    private static async Task<QuestRequirementDto> SlimesAsync(GameFixture session) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == 1001).Requirements.Single(r => r.ItemId == 20);
}
