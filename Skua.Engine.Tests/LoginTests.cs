using System.Diagnostics;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>servers</c>, <c>login</c> and <c>logout</c> against the fake Game Host's simulated game.</summary>
public class LoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Servers_lists_the_servers_before_login()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(new FakeServer("Galanoth", Count: 120, Max: 1000), new FakeServer("Yorumi", Online: false, Member: true, Lang: "pt"));
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain), api, keychain));
        using (connection)
        {
            ServersResult result = await connection.ServersAsync(Ct);

            Assert.Equal(
                [new ServerDto("Galanoth", true, 120, 1000, false, "en"), new ServerDto("Yorumi", false, 100, 1000, true, "pt")],
                result.Servers);
            Assert.Equal(0, keychain.Reads);
        }
    }

    [Fact]
    public async Task Servers_fails_with_ServersUnavailable_when_the_servers_API_is_down()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(new FakeServer("Galanoth")) { Down = true };
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain), api, keychain));
        using (connection)
        {
            ControlException e = await Assert.ThrowsAsync<ControlException>(() => connection.ServersAsync(Ct));

            Assert.Equal(ErrorCode.ServersUnavailable, e.Code);
        }
    }

    [Fact]
    public async Task Login_on_a_named_server_returns_once_playing_there()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        using (connection)
        {
            await connection.WaitForEventAsync(EventTypes.GameLoaded);

            LoginResult result = await connection.LoginAsync("galanoth", cancellationToken: Ct);
            StatusDto status = await connection.StatusAsync(Ct);

            Assert.Equal(new LoginResult("Galanoth", false, "SkuaTester", IsTestAccount: true), result);
            Assert.Equal(GameState.Playing, status.Game.State);
            Assert.Equal("Galanoth", status.Game.Server);
            Assert.Equal(
                [EventTypes.GameLoaded, "notStarted→loginScreen", "loginScreen→loggingIn", "loggingIn→playing"],
                (await GameEvents.AllAsync(connection)).Select(GameEvents.Describe));
        }
    }

    [Fact]
    public async Task Login_without_a_server_picks_the_emptiest_online_non_member_server_with_room()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        LoginResult result = await session.Connection.LoginAsync(cancellationToken: Ct);

        Assert.Equal(new LoginResult("Sir Ver", false, "SkuaTester", IsTestAccount: true), result);
    }

    [Fact]
    public async Task Login_on_the_current_server_does_nothing_and_on_another_relogs_without_a_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        LoginResult same = await session.Connection.LoginAsync("GALANOTH", cancellationToken: Ct);
        LoginResult any = await session.Connection.LoginAsync(cancellationToken: Ct);
        LoginResult other = await session.Connection.LoginAsync("Sir Ver", cancellationToken: Ct);
        StatusDto status = await session.Connection.StatusAsync(Ct);

        Assert.Equal(new LoginResult("Galanoth", true, "SkuaTester", IsTestAccount: true), same);
        Assert.Equal(new LoginResult("Galanoth", true, "SkuaTester", IsTestAccount: true), any);
        Assert.Equal(new LoginResult("Sir Ver", false, "SkuaTester", IsTestAccount: true), other);
        Assert.Equal(("Sir Ver", GameState.Playing), (status.Game.Server, status.Game.State));
        Assert.Equal(
            [EventTypes.GameLoaded, "notStarted→loginScreen", "loginScreen→loggingIn", "loggingIn→playing", "playing→loggingIn", "loggingIn→playing"],
            (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe));
        // Each login reads the account afresh, since 'skua account' may have changed it.
        Assert.Equal(2, session.Keychain.Reads);
    }

    [Fact]
    public async Task A_server_the_game_refuses_fails_the_login_with_its_reason_and_is_no_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.Reject("Galanoth", "Server is Full. Try another server."));

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.LoginAsync("Galanoth", cancellationToken: Ct));
        StatusDto status = await session.Connection.StatusAsync(Ct);

        Assert.Equal(ErrorCode.LoginFailed, e.Code);
        Assert.Contains("Server is Full. Try another server.", e.Message);
        Assert.Equal(GameState.LoginScreen, status.Game.State);
        Assert.DoesNotContain(await GameEvents.AllAsync(session.Connection), entry => entry.Type == EventTypes.GameDisconnected);
    }

    [Theory]
    [InlineData("Artix", ErrorCode.LoginFailed, "Artix is full (1500/1500)")]
    [InlineData("Twig", ErrorCode.LoginFailed, "Twig is offline")]
    [InlineData("Nowhere", ErrorCode.InvalidArgument, "No server is named 'Nowhere'")]
    [InlineData("TestServer", ErrorCode.InvalidArgument, "TestServer is a test server")]
    public async Task A_server_that_cant_be_used_fails_the_login_before_the_Keychain_is_read(string server, ErrorCode code, string message)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.LoginAsync(server, cancellationToken: Ct));

        Assert.Equal(code, e.Code);
        Assert.Contains(message, e.Message);
        Assert.Equal(0, session.Keychain.Reads);
    }

    [Fact]
    public async Task Without_a_Test_Account_in_Keychain_the_login_fails_naming_the_service()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox, service: "someone-else");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        using (connection)
        {
            await connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "loginScreen");

            ControlException e = await Assert.ThrowsAsync<ControlException>(() => connection.LoginAsync("Galanoth", cancellationToken: Ct));

            Assert.Equal(ErrorCode.LoginFailed, e.Code);
            Assert.Contains("'skua-test-account'", e.Message);
            Assert.Equal(GameState.LoginScreen, (await connection.StatusAsync(Ct)).Game.State);
        }
    }

    [Fact]
    public async Task A_password_security_shows_as_hex_is_read_as_text()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox, password: "pässwörd-€");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        using (connection)
        {
            LoginResult result = await connection.LoginAsync("Galanoth", cancellationToken: Ct);

            Assert.Equal("Galanoth", result.Server);
        }
    }

    [Fact]
    public async Task The_Keychain_service_is_an_Engine_setting()
    {
        await using EngineSandbox sandbox = new();
        File.WriteAllText(Path.Combine(sandbox.SkuaDir, "Skua.settings.json"), """{"client":{"TestAccountService":"my-test-account"}}""");
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox, service: "my-test-account");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox).Game(keychain, GameFixture.Servers), api, keychain));
        using (connection)
        {
            await connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "loginScreen");

            LoginResult result = await connection.LoginAsync("Galanoth", cancellationToken: Ct);

            Assert.Equal("Galanoth", result.Server);
        }
    }

    [Fact]
    public async Task A_login_that_doesnt_reach_the_world_in_time_fails_with_Timeout()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.ConnectDelay(30_000));

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.LoginAsync("Galanoth", 2, Ct));

        Assert.Equal(ErrorCode.Timeout, e.Code);
        Assert.Contains("'Connecting to game server...'", e.Message);
    }

    [Fact]
    public async Task Login_before_the_Game_Client_has_loaded_fails_with_GameHostDown()
    {
        await using EngineSandbox sandbox = new();
        await using FakeAqApi api = new(GameFixture.Servers);
        FakeKeychain keychain = new(sandbox);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(GameFixture.Environment(new FakeGameHost(sandbox), api, keychain));
        using (connection)
        {
            ControlException e = await Assert.ThrowsAsync<ControlException>(() => connection.LoginAsync("Galanoth", cancellationToken: Ct));

            Assert.Equal(ErrorCode.GameHostDown, e.Code);
        }
    }

    [Fact]
    public async Task A_second_login_while_one_runs_fails_with_Busy()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox, fake => fake.ConnectDelay(2000));

        Task<LoginResult> first = session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.Connection.WaitForEventAsync(EventTypes.GameState, e => GameEvents.To(e) == "loggingIn");
        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.LogoutAsync(Ct));

        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("Galanoth", (await first).Server);
    }

    [Fact]
    public async Task Logout_returns_to_the_login_screen_as_a_deliberate_disconnect()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        LogoutResult result = await session.Connection.LogoutAsync(Ct);
        LogoutResult again = await session.Connection.LogoutAsync(Ct);
        StatusDto status = await session.Connection.StatusAsync(Ct);

        Assert.True(result.WasLoggedIn);
        Assert.False(again.WasLoggedIn);
        Assert.Equal((GameState.LoginScreen, null), (status.Game.State, status.Game.Server));
        Assert.Equal(
            [EventTypes.GameLoaded, "notStarted→loginScreen", "loginScreen→loggingIn", "loggingIn→playing", "game.disconnected logout", "playing→loginScreen"],
            (await GameEvents.AllAsync(session.Connection)).Select(GameEvents.Describe));
    }

    [Fact]
    public async Task The_password_and_the_login_token_appear_in_no_log_event_file_or_reply()
    {
        const string Token = "Tok3n-abc123";
        await using EngineSandbox sandbox = new();
        GameFixture session = await GameFixture.StartAsync(sandbox);
        await using (session)
        {
            string password = session.Keychain.Password;
            EngineConnection connection = session.Connection;
            LoginResult login = await connection.LoginAsync("Galanoth", cancellationToken: Ct);
            // The game's own trace of its login, and a careless one of the password.
            await session.GameHost.DoAsync($"send F [Net] [Sending] <msg t='sys'><body action='login' r='0'><login z='zone_master'><nick><![CDATA[SPIDER#0001~{session.Keychain.Username}~4.372]]></nick><pword><![CDATA[{Token}]]></pword></login></body></msg>");
            await session.GameHost.DoAsync($"send F [Net] [Sending] <msg><login><pword><![CDATA[{Token}");
            await session.GameHost.DoAsync($"send F debug: password is {password}");
            await connection.WaitForLogsAsync(LogKind.Flash, 1, e => e.Text!.StartsWith("debug: password is", StringComparison.Ordinal));
            await connection.LogoutAsync(Ct);
            // A login call that fails on the Bridge is logged with its arguments.
            await session.GameHost.DoAsync("broken-login");
            await Assert.ThrowsAsync<ControlException>(() => connection.LoginAsync("Galanoth", 2, Ct));

            LogEntryDto bridgeError = await connection.WaitForEventAsync(EventTypes.BridgeError, e => e.Data!.Value.GetProperty("function").GetString() == "callGameFunction");
            string logs = JsonSerializer.Serialize(await connection.LogsAsync(LogKind.All, null, 1000, Ct), ControlJson.Options);
            string status = JsonSerializer.Serialize(await connection.StatusAsync(Ct), ControlJson.Options);
            await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, Ct);
            await session.Engine.WaitForExitAsync(Ct);
            string stderr = await session.Engine.StandardError.ReadToEndAsync(Ct);
            string files = string.Concat(Directory.EnumerateFiles(sandbox.SkuaDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(Path.GetDirectoryName(session.Keychain.Tool) + "/fake-", StringComparison.Ordinal))
                .Select(File.ReadAllText));

            Assert.Equal(["login", "[redacted]", "[redacted]"], bridgeError.Data!.Value.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            Assert.Contains("<pword><![CDATA[[redacted]]]></pword>", logs);
            Assert.Contains("debug: password is [redacted]", logs);
            foreach (string text in new[] { logs, status, JsonSerializer.Serialize(login), stderr, files })
            {
                Assert.DoesNotContain(password, text);
                Assert.DoesNotContain(Token, text);
            }
        }
    }

    [Fact]
    public async Task The_Engine_holds_off_idle_sleep_while_logged_in()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        string assertion = $"pid {session.Engine.Id}(skua-engine)";

        bool before = (await AssertionsAsync()).Contains(assertion);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        string during = await AssertionsAsync();
        await session.Connection.LogoutAsync(Ct);
        bool after = (await AssertionsAsync()).Contains(assertion);

        Assert.False(before);
        Assert.Contains($"{assertion}: ", during);
        Assert.Matches($@"pid {session.Engine.Id}\(skua-engine\): \[\w+\] [\d:]+ PreventUserIdleSystemSleep named: ""Skua Engine 'default' is logged in""", during);
        Assert.False(after);
    }

    [Fact]
    public async Task The_lag_killer_is_on_after_login_and_lifted_for_a_screenshot()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.WaitForCallAsync("killLag true");

        await session.Connection.ScreenshotAsync(cancellationToken: Ct);
        string[] calls = await session.GameHost.WaitForCallAsync("killLag true", after: "screenshot 0");

        Assert.Contains("screenshot 0", calls);
        Assert.DoesNotContain("screenshot 0 lag-killed", calls);
        int shot = Array.IndexOf(calls, "screenshot 0");
        Assert.Equal("killLag false", calls[..shot].Last(c => c.StartsWith("killLag", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_killLag_true_from_another_thread_during_a_screenshot_doesnt_hide_the_world_for_it()
    {
        await using EngineSandbox sandbox = new();
        // Slow killLag replies hold the capture after it lifts the lag killer, as Core's timer, having read the option just before, is sending it.
        await using GameFixture session = await GameFixture.StartAsync(sandbox, g => g.Delay("killLag", 2000));
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.WaitForCallAsync("killLag true");
        await session.Connection.EvalAsync("1", cancellationToken: Ct);

        Task<ScreenshotResult> shot = session.Connection.ScreenshotAsync(cancellationToken: Ct);
        await session.GameHost.WaitForCallAsync("killLag false");
        await session.Connection.EvalAsync("""Bot.Flash.Call("killLag", true)""", cancellationToken: Ct);
        await shot;
        string[] calls = await session.GameHost.WaitForCallAsync("killLag true", after: "screenshot 0");

        Assert.Contains("screenshot 0", calls);
        int shotAt = Array.IndexOf(calls, "screenshot 0");
        Assert.Equal("killLag false", calls[..shotAt].Last(c => c.StartsWith("killLag", StringComparison.Ordinal)));
    }

    private static async Task<string> AssertionsAsync()
    {
        using Process pmset = Process.Start(new ProcessStartInfo("pmset", "-g assertions") { RedirectStandardOutput = true })!;
        string output = await pmset.StandardOutput.ReadToEndAsync(Ct);
        await pmset.WaitForExitAsync(Ct);
        return output;
    }
}
