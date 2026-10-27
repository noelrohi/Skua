using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>run.struggling</c> (#287): the deaths of a run piling up within the window, against the fake game's <c>die</c>.</summary>
public class RunStruggleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deaths_that_reach_the_threshold_within_the_window_record_run_struggling_once()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string> { ["SKUA_STRUGGLE_DEATHS"] = "3" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        // A death before the run isn't the run's.
        await DieAsync(session);

        ScriptStartResult start = await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        await DieAsync(session);
        await DieAsync(session);
        bool beforeThreshold = (await AllEventsAsync(session)).Any(e => e.Type == EventTypes.RunStruggling);
        await DieAsync(session);
        LogEntryDto struggling = await session.Connection.WaitForEventAsync(EventTypes.RunStruggling);
        // Still struggling: no second event.
        await DieAsync(session);
        IReadOnlyList<LogEntryDto> events = await AllEventsAsync(session);
        ProcessResult cli = await sandbox.RunCliAsync("logs", "events", "--max", "1000", "--json");

        Assert.False(beforeThreshold);
        Assert.Single(events, e => e.Type == EventTypes.RunStruggling);
        JsonElement data = struggling.Data!.Value;
        Assert.Equal(start.Run, data.GetProperty("run").GetInt32());
        Assert.Equal("Tests/Loop.cs", data.GetProperty("script").GetString());
        Assert.Equal(3, data.GetProperty("deaths").GetInt32());
        Assert.Equal(300, data.GetProperty("windowSec").GetInt32());
        Assert.Equal("battleon", data.GetProperty("map").GetString());
        // Tests/Loop.cs logs no CoreBots lines, so it has no goal.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("goal").ValueKind);
        Assert.Equal(0, cli.ExitCode);
        Assert.Contains("\"run.struggling\"", cli.Stdout);
    }

    [Fact]
    public async Task Deaths_spread_past_the_window_record_none_and_a_burst_after_recovery_records_another()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string>
        {
            ["SKUA_STRUGGLE_DEATHS"] = "2",
            ["SKUA_STRUGGLE_WINDOW_SEC"] = "3",
        });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        await DieAsync(session);
        LogEntryDto second = await DieAsync(session);
        LogEntryDto first = await session.Connection.WaitForEventAsync(EventTypes.RunStruggling);
        // The next death comes after the window has passed the last: one death in the window, so none, and the run has recovered.
        await PastWindowAsync(second, 3);
        LogEntryDto spread = await DieAsync(session);
        bool afterSpread = (await AllEventsAsync(session)).Count(e => e.Type == EventTypes.RunStruggling) > 1;
        await DieAsync(session);
        LogEntryDto again = await session.Connection.WaitForEventAsync(EventTypes.RunStruggling, e => e.Seq > first.Seq);

        Assert.False(afterSpread);
        Assert.True(again.Seq > spread.Seq);
        Assert.Equal((2, 3), (again.Data!.Value.GetProperty("deaths").GetInt32(), again.Data!.Value.GetProperty("windowSec").GetInt32()));
    }

    [Fact]
    public async Task Zero_turns_it_off()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string> { ["SKUA_STRUGGLE_DEATHS"] = "0" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        await session.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        for (int i = 0; i < 6; i++)
            await DieAsync(session);

        Assert.DoesNotContain(await AllEventsAsync(session), e => e.Type == EventTypes.RunStruggling);
    }

    /// <summary>
    /// Kills the player and waits for its <c>player.death</c>, then for a read of the game, which answers only once the Engine has handled
    /// the death's packet, sent first on the same pipe.
    /// </summary>
    private static async Task<LogEntryDto> DieAsync(GameFixture session)
    {
        long after = (await AllEventsAsync(session)).Where(e => e.Type == EventTypes.PlayerDeath).Select(e => e.Seq).DefaultIfEmpty(0).Max();
        await session.GameHost.DoAsync("die");
        LogEntryDto death = await session.Connection.WaitForEventAsync(EventTypes.PlayerDeath, e => e.Seq > after);
        await session.Connection.EvalAsync("return Bot.Player.Level;", cancellationToken: Ct);
        return death;
    }

    /// <summary>Waits until <paramref name="windowSec"/> has passed since <paramref name="death"/>; a slow machine only waits longer.</summary>
    private static Task PastWindowAsync(LogEntryDto death, int windowSec) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, death.Ts + windowSec * 1000 + 250 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), Ct);

    private static async Task<IReadOnlyList<LogEntryDto>> AllEventsAsync(GameFixture session) =>
        (await session.Connection.LogsAsync(LogKind.Events, null, 1000, Ct)).Entries;
}
