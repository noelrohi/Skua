using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>How long the quests' requirements go without a rise, and <c>quest.stalled</c>, against the fake game's Slime Time and its Slime Samples.</summary>
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
        await session.GameHost.DoAsync("slime-samples 4");
        // status reads what the last sample saw, so the rise shows within a sample.
        ScriptRunDto progressed = await WaitForAsync(async () => (await session.Connection.ScriptStatusAsync(Ct)).Run!, r => r.QuestIdleSec < 1);
        LogEntryDto again = await session.Connection.WaitForEventAsync(EventTypes.QuestStalled, e => e.Seq > stalled.Seq);

        JsonElement data = stalled.Data!.Value;
        Assert.Equal(start.Run, data.GetProperty("run").GetInt32());
        Assert.Equal("Tests/Loop.cs", data.GetProperty("script").GetString());
        Assert.True(data.GetProperty("idleSec").GetDouble() >= 2);
        // Slime Time needs 5 Slime Samples; Chest Hoarder is completable and Not Yet isn't accepted, so neither is stalled.
        JsonElement quest = Assert.Single(data.GetProperty("quests").EnumerateArray().ToList());
        Assert.Equal((1001, "Slime Time"), (quest.GetProperty("id").GetInt32(), quest.GetProperty("name").GetString()));
        JsonElement requirement = Assert.Single(quest.GetProperty("requirements").EnumerateArray().ToList());
        Assert.Equal(("Slime Sample", 3, 5),
            (requirement.GetProperty("name").GetString(), requirement.GetProperty("have").GetInt32(), requirement.GetProperty("qty").GetInt32()));
        Assert.InRange(run.QuestIdleSec!.Value, 2, run.ElapsedSec);
        Assert.InRange(progressed.QuestIdleSec!.Value, 0, 1);
        // A rise re-arms it, so the next stall is recorded too.
        Assert.Equal(4, again.Data!.Value.GetProperty("quests")[0].GetProperty("requirements")[0].GetProperty("have").GetInt32());
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

    private static async Task<QuestRequirementDto> SlimesAsync(GameFixture session) =>
        (await session.Connection.QuestsAsync(cancellationToken: Ct)).Quests.Single(q => q.Id == 1001).Requirements.Single();
}
