using System.Diagnostics;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>status</c>'s game state and the <c>game.*</c> events, which one tracker in the Engine computes.</summary>
public class GameStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_loaded_Game_Client_is_at_the_login_screen()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(new FakeGameHost(sandbox).Game(keychain).Environment());
        using (connection)
        {
            await connection.WaitForEventAsync(EventTypes.GameState, e => To(e) == "loginScreen");

            StatusDto status = await connection.StatusAsync(Ct);
            List<LogEntryDto> events = await GameEventsAsync(connection);

            Assert.Equal(GameState.LoginScreen, status.Game.State);
            Assert.Null(status.Game.Server);
            Assert.Equal([EventTypes.GameLoaded, "notStarted→loginScreen"], events.Select(Describe));
        }
    }

    [Theory]
    [InlineData("lose-connection Your connection to the server has been lost.", "connectionLost", GameState.Disconnected)]
    [InlineData("kick", "kicked", GameState.Disconnected)]
    [InlineData("logout-button", "logout", GameState.LoginScreen)]
    public async Task Losing_the_session_is_one_disconnect_with_its_reason(string directive, string reason, GameState state)
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync(directive);
        LogEntryDto disconnected = await session.Connection.WaitForEventAsync(EventTypes.GameDisconnected);
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => To(e) != "playing" && To(e) != "loggingIn");
        await Task.Delay(1500, Ct);

        Assert.Equal(reason, disconnected.Data!.Value.GetProperty("reason").GetString());
        if (reason == "connectionLost")
            Assert.Equal("Your connection to the server has been lost.", disconnected.Data!.Value.GetProperty("detail").GetString());
        else
            Assert.False(disconnected.Data!.Value.TryGetProperty("detail", out _));
        Assert.Equal(state, (await session.Connection.StatusAsync(Ct)).Game.State);
        List<LogEntryDto> events = await GameEventsAsync(session.Connection);
        Assert.Single(events, e => e.Type == EventTypes.GameDisconnected);
        Assert.Equal($"playing→{JsonNamingPolicy.CamelCase.ConvertName(state.ToString())}", Describe(events[^1]));
    }

    [Fact]
    public async Task A_disconnected_game_stays_disconnected_until_a_login()
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("kick");
        await session.Connection.WaitForEventAsync(EventTypes.GameDisconnected);

        await Task.Delay(1500, Ct);
        GameState waiting = (await session.Connection.StatusAsync(Ct)).Game.State;
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        Assert.Equal(GameState.Disconnected, waiting);
        Assert.Equal(
            [EventTypes.GameLoaded, "notStarted→loginScreen", "loginScreen→loggingIn", "loggingIn→playing", "game.disconnected kicked", "playing→disconnected",
             "disconnected→loggingIn", "loggingIn→playing"],
            (await GameEventsAsync(session.Connection)).Select(Describe));
    }

    [Fact]
    public async Task A_Game_Host_exit_forces_notStarted_and_is_a_gameHostExited_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        using (Process gameHost = Process.GetProcessById(await session.GameHost.PidAsync()))
            gameHost.Kill();
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => To(e) == "notStarted");
        StatusDto status = await session.Connection.StatusAsync(Ct);

        Assert.Equal((false, GameState.NotStarted, null), (status.Game.GameHostUp, status.Game.State, status.Game.Server));
        Assert.Equal(["game.disconnected gameHostExited", "playing→notStarted"], (await GameEventsAsync(session.Connection)).Select(Describe).TakeLast(2));
    }

    [Fact]
    public async Task A_momentary_reading_doesnt_change_the_state()
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        int before = (await GameEventsAsync(session.Connection)).Count;

        // Each blip lasts less than a poll interval, and they're far enough apart that no two polls in a row see one.
        for (int i = 0; i < 4; i++)
        {
            await session.GameHost.DoAsync("blip 300");
            await Task.Delay(1500, Ct);
        }

        Assert.Equal(before, (await GameEventsAsync(session.Connection)).Count);
        Assert.Equal(GameState.Playing, (await session.Connection.StatusAsync(Ct)).Game.State);
    }

    [Fact]
    public async Task Status_and_the_game_state_events_agree_through_login_logout_relogin_and_a_Game_Host_kill()
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        EngineConnection connection = session.Connection;

        await AgreeAsync(connection);
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await AgreeAsync(connection);
        await connection.LoginAsync("Sir Ver", cancellationToken: Ct);
        await AgreeAsync(connection);
        await connection.LogoutAsync(Ct);
        await AgreeAsync(connection);
        await connection.LoginAsync(cancellationToken: Ct);
        using (Process gameHost = Process.GetProcessById(await session.GameHost.PidAsync()))
            gameHost.Kill();
        await connection.WaitForEventAsync(EventTypes.GameState, e => To(e) == "notStarted");
        await AgreeAsync(connection);
    }

    [Fact]
    public async Task Joining_a_map_dying_and_going_AFK_are_events()
    {
        await using EngineSandbox sandbox = new();
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("join yulgar");
        await session.GameHost.DoAsync("cell Upstairs");
        await session.GameHost.DoAsync("die");
        await session.GameHost.DoAsync("afk");
        await session.Connection.WaitForEventAsync(EventTypes.PlayerAfk);

        List<LogEntryDto> joined = await session.Connection.WaitForLogsAsync(LogKind.Events, 2, e => e.Type == EventTypes.MapJoined);
        LogEntryDto death = await session.Connection.WaitForEventAsync(EventTypes.PlayerDeath);
        Assert.Equal(["battleon Enter", "yulgar Enter"], joined.Select(e => $"{e.Data!.Value.GetProperty("map").GetString()} {e.Data!.Value.GetProperty("cell").GetString()}"));
        Assert.True(joined[1].Data!.Value.GetProperty("roomId").GetInt32() > joined[0].Data!.Value.GetProperty("roomId").GetInt32());
        Assert.Equal(("yulgar", "Upstairs"), (death.Data!.Value.GetProperty("map").GetString(), death.Data!.Value.GetProperty("cell").GetString()));
    }

    [Fact]
    public async Task Core_auto_relogin_after_a_lost_connection_is_one_disconnect_and_a_relogin_with_both_phases()
    {
        await using EngineSandbox sandbox = new();
        File.WriteAllText(Path.Combine(sandbox.SkuaDir, "Skua.settings.json"),
            """{"client":{"UserOptions":["AutoRelogin=True","SafeRelogin=False","ReloginTryDelay=200"]}}""");
        await using LoginTests.Session session = await LoginTests.Session.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("lose-connection Your connection to the server has been lost.");
        LogEntryDto finished = await session.Connection.WaitForEventAsync(EventTypes.GameRelogin, e => e.Data!.Value.GetProperty("phase").GetString() == "finished");
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => e.Data!.Value.GetProperty("from").GetString() == "loggingIn" && To(e) == "playing"
            && e.Seq > finished.Seq);
        List<LogEntryDto> events = await GameEventsAsync(session.Connection);

        Assert.Single(events, e => e.Type == EventTypes.GameDisconnected);
        LogEntryDto triggered = events.Single(e => e.Type == EventTypes.GameRelogin && e.Data!.Value.GetProperty("phase").GetString() == "triggered");
        Assert.False(triggered.Data!.Value.GetProperty("wasKicked").GetBoolean());
        Assert.Equal(200, triggered.Data!.Value.GetProperty("delayMs").GetInt32());
        Assert.True(events.Single(e => e.Type == EventTypes.GameRelogin && e.Data!.Value.GetProperty("phase").GetString() == "finished").Data!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ["game.disconnected connectionLost", "playing→disconnected", EventTypes.GameRelogin, "disconnected→loggingIn", EventTypes.GameRelogin, "loggingIn→playing"],
            events.Select(Describe).SkipWhile(d => !d.StartsWith("game.disconnected", StringComparison.Ordinal)));
        Assert.Equal(GameState.Playing, (await session.Connection.StatusAsync(Ct)).Game.State);
    }

    /// <summary>Waits a moment for the poll, then checks that status reports the state the last <c>game.state</c> event moved to.</summary>
    private static async Task AgreeAsync(EngineConnection connection)
    {
        await Task.Delay(1200, Ct);
        StatusDto status = await connection.StatusAsync(Ct);
        LogEntryDto last = (await GameEventsAsync(connection)).Last(e => e.Type == EventTypes.GameState);
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(status.Game.State.ToString()), To(last));
    }

    internal static string? To(LogEntryDto entry) => entry.Data!.Value.GetProperty("to").GetString();

    /// <summary>The <c>game.*</c> events so far, as their type or, for <c>game.state</c>, <c>from→to</c>.</summary>
    internal static async Task<List<LogEntryDto>> GameEventsAsync(EngineConnection connection)
    {
        List<LogEntryDto> events = [];
        string? cursor = null;
        while (true)
        {
            LogPage page = await connection.LogsAsync(LogKind.Events, cursor, 1000, Ct);
            events.AddRange(page.Entries.Where(e => e.Type!.StartsWith("game.", StringComparison.Ordinal)));
            cursor = page.Next;
            if (page.Entries.Count == 0)
                return events;
        }
    }

    internal static string Describe(LogEntryDto entry) => entry.Type switch
    {
        EventTypes.GameState => $"{entry.Data!.Value.GetProperty("from").GetString()}→{To(entry)}",
        EventTypes.GameDisconnected => $"{entry.Type} {entry.Data!.Value.GetProperty("reason").GetString()}",
        _ => entry.Type!,
    };
}
