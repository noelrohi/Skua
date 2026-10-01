using System.Diagnostics;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>status</c>'s game state and the <c>game.*</c> events, which one tracker in the Engine computes.</summary>
public class GameStateTests
{
    private const string RespawnAfterVariable = "SKUA_RESPAWN_AFTER_MS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_loaded_Game_Client_is_at_the_login_screen()
    {
        await using EngineSandbox sandbox = new();
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(new FakeGameHost(sandbox).Game(keychain).Environment());
        using (connection)
        {
            await connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "loginScreen");

            StatusDto status = await connection.StatusAsync(Ct);
            List<LogEntryDto> events = await GameEvents.AllAsync(connection);

            Assert.Equal(GameState.LoginScreen, status.Game.State);
            Assert.Null(status.Game.Server);
            Assert.Equal([EventTypes.GameLoaded, "notStarted→loginScreen"], events.Select(GameEvents.Describe));
        }
    }

    [Theory]
    [InlineData("lose-connection Your connection to the server has been lost.", "connectionLost", GameState.Disconnected)]
    [InlineData("kick", "kicked", GameState.Disconnected)]
    [InlineData("logout-button", "logout", GameState.LoginScreen)]
    public async Task Losing_the_session_is_one_disconnect_with_its_reason(string directive, string reason, GameState state)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync(directive);
        LogEntryDto disconnected = await session.Connection.WaitForEventAsync(EventTypes.GameDisconnected);
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) != "playing" && GameEvents.To(e) != "loggingIn");
        await Task.Delay(1500, Ct);

        Assert.Equal(reason, disconnected.Data!.Value.GetProperty("reason").GetString());
        if (reason == "connectionLost")
            Assert.Equal("Your connection to the server has been lost.", disconnected.Data!.Value.GetProperty("detail").GetString());
        else
            Assert.False(disconnected.Data!.Value.TryGetProperty("detail", out _));
        Assert.Equal(state, (await session.Connection.StatusAsync(Ct)).Game.State);
        List<LogEntryDto> events = await GameEvents.AllAsync(session.Connection);
        Assert.Single(events, e => e.Type == EventTypes.GameDisconnected);
        Assert.Equal($"playing→{JsonNamingPolicy.CamelCase.ConvertName(state.ToString())}", GameEvents.Describe(events[^1]));
    }

    [Fact]
    public async Task A_lost_connection_message_is_a_disconnect_even_while_the_game_still_says_it_is_connected()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("connection-message Your connection to the server has been lost.");
        LogEntryDto disconnected = await session.Connection.WaitForEventAsync(EventTypes.GameDisconnected);
        await Task.Delay(1500, Ct);

        Assert.Equal("connectionLost", disconnected.Data!.Value.GetProperty("reason").GetString());
        Assert.Equal(GameState.Disconnected, (await session.Connection.StatusAsync(Ct)).Game.State);
        Assert.Equal(["game.disconnected connectionLost", "playing→disconnected"], (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe).TakeLast(2));
    }

    [Fact]
    public async Task A_login_response_that_reaches_the_Engine_after_the_connection_was_lost_isnt_a_second_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("lose-connection Your connection to the server has been lost.");
        await session.Connection.WaitForEventAsync(EventTypes.GameDisconnected);

        // Core handles each game call before the Engine, so a busy Engine can see the login's response only now.
        await session.GameHost.DoAsync("login-response");
        // The Engine handles game calls in order, so once this join is an event, the response has been handled.
        await session.GameHost.DoAsync("join yulgar");
        await session.Connection.WaitForEventAsync(EventTypes.MapJoined, e => e.Data!.Value.GetProperty("map").GetString() == "yulgar");
        GameState state = (await session.Connection.StatusAsync(Ct)).Game.State;
        // The Test Account was already disconnected, so the Game Host exiting isn't a disconnect either.
        using (Process gameHost = Process.GetProcessById(await session.GameHost.PidAsync()))
            gameHost.Kill();
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "notStarted");

        Assert.Equal(GameState.Disconnected, state);
        Assert.Equal(
            ["loggingIn→playing", "game.disconnected connectionLost", "playing→disconnected", "disconnected→notStarted"],
            (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe).TakeLast(4));
    }

    [Fact]
    public async Task A_disconnected_game_stays_disconnected_until_a_login()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
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
            (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe));
    }

    [Fact]
    public async Task A_Game_Host_exit_forces_notStarted_and_is_a_gameHostExited_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        using (Process gameHost = Process.GetProcessById(await session.GameHost.PidAsync()))
            gameHost.Kill();
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "notStarted");
        StatusDto status = await session.Connection.StatusAsync(Ct);

        Assert.Equal((false, GameState.NotStarted, null), (status.Game.GameHostUp, status.Game.State, status.Game.Server));
        Assert.Equal(["game.disconnected gameHostExited", "playing→notStarted"], (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe).TakeLast(2));
    }

    [Fact]
    public async Task A_momentary_reading_doesnt_change_the_state()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        int before = (await GameEvents.AllAsync(session.Connection)).Count;

        // The blip lasts for one of the tracker's readings (Core's timer reads the flag too, and doesn't end it).
        await session.GameHost.DoAsync("blip");
        await session.GameHost.WaitForCallAsync("blip read 1");
        // The tracker's next change is then the real loss, which applies at once.
        await session.GameHost.DoAsync("connection-message Your connection to the server has been lost.");
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "disconnected");

        Assert.Equal(["game.disconnected connectionLost", "playing→disconnected"],
            (await GameEvents.AllAsync(session.Connection)).Skip(before).Take(2).Select(GameEvents.Describe));
    }

    [Fact]
    public async Task Status_and_the_game_state_events_agree_through_login_logout_relogin_and_a_Game_Host_kill()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
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
        await connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "notStarted");
        await AgreeAsync(connection);
    }

    [Fact]
    public async Task Joining_a_map_dying_and_going_AFK_are_events()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
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

    /// <summary>
    /// The game's own respawn request can come before the game server allows one, which then ignores it (#153); the Engine asks again.
    /// </summary>
    [Fact]
    public async Task A_player_the_game_server_left_dead_after_the_games_early_respawn_request_is_respawned()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, environment: new Dictionary<string, string> { [RespawnAfterVariable] = "2500" });
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("die");
        await session.GameHost.DoAsync("respawn-request");
        string[] calls = await session.GameHost.WaitForCallAsync("respawn");

        Assert.Equal(["send %xt%zm%resPlayerTimed%1001%1%", "respawn ignored", "send %xt%zm%resPlayerTimed%1001%1%", "respawn"],
            calls.Where(c => c.StartsWith("respawn", StringComparison.Ordinal) || c.Contains("resPlayerTimed", StringComparison.Ordinal)));
        Assert.True((await session.Connection.StatusAsync(Ct)).Game.Player!.Alive);
    }

    [Fact]
    public async Task Core_auto_relogin_after_a_lost_connection_is_one_disconnect_and_a_relogin_with_both_phases()
    {
        await using EngineSandbox sandbox = new();
        File.WriteAllText(Path.Combine(sandbox.SkuaDir, "Skua.settings.json"),
            """{"client":{"UserOptions":["AutoRelogin=True","SafeRelogin=False","ReloginTryDelay=200"]}}""");
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        await session.GameHost.DoAsync("lose-connection Your connection to the server has been lost.");
        LogEntryDto finished = await session.Connection.WaitForEventAsync(EventTypes.GameRelogin, e => e.Data!.Value.GetProperty("phase").GetString() == "finished");
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => e.Data!.Value.GetProperty("from").GetString() == "loggingIn" && GameEvents.To(e) == "playing"
            && e.Seq > finished.Seq);
        List<LogEntryDto> events = await GameEvents.AllAsync(session.Connection);

        Assert.Single(events, e => e.Type == EventTypes.GameDisconnected);
        LogEntryDto triggered = events.Single(e => e.Type == EventTypes.GameRelogin && e.Data!.Value.GetProperty("phase").GetString() == "triggered");
        Assert.False(triggered.Data!.Value.GetProperty("wasKicked").GetBoolean());
        Assert.Equal(200, triggered.Data!.Value.GetProperty("delayMs").GetInt32());
        Assert.True(events.Single(e => e.Type == EventTypes.GameRelogin && e.Data!.Value.GetProperty("phase").GetString() == "finished").Data!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ["game.disconnected connectionLost", "playing→disconnected", EventTypes.GameRelogin, "disconnected→loggingIn", EventTypes.GameRelogin, "loggingIn→playing"],
            events.Select(GameEvents.Describe).SkipWhile(d => !d.StartsWith("game.disconnected", StringComparison.Ordinal)));
        Assert.Equal(GameState.Playing, (await session.Connection.StatusAsync(Ct)).Game.State);
        // Back to playing after a relogin, the lag killer is on again, though Core turned it off while stopping for the relogin.
        // It goes on once the state is playing, so the call may still be on its way.
        await session.GameHost.WaitForCallAsync("killLag true", after: "clickServer");
    }

    /// <summary>Waits a moment for the poll, then checks that status reports the state the last <c>game.state</c> event moved to.</summary>
    private static async Task AgreeAsync(EngineConnection connection)
    {
        await Task.Delay(1200, Ct);
        StatusDto status = await connection.StatusAsync(Ct);
        LogEntryDto last = (await GameEvents.AllAsync(connection)).Last(e => e.Type == EventTypes.GameState);
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(status.Game.State.ToString()), GameEvents.To(last));
    }
}
