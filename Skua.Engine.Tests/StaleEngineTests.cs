using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The CLI replaces a running Engine from another build when it is idle, and never stops one whose Script is running.</summary>
public class StaleEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ControlProtocol.Version)]
    [InlineData(OtherVersionEngine.OtherProtocol)]
    public async Task The_CLI_replaces_an_idle_Engine_from_another_build_and_says_so_on_one_line(int protocol)
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, protocol);

        ProcessResult status = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(0, status.ExitCode);
        Assert.True(other.ShutdownRequested);
        Assert.False(other.StatusCalled);
        string notice = Assert.Single(status.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith($"skua: Replaced Engine 'default' from another build ({OtherVersionEngine.OtherBuild}, protocol {protocol})", notice);
        using JsonDocument json = JsonDocument.Parse(status.Stdout);
        JsonElement engine = json.RootElement.GetProperty("engine");
        Assert.Equal(ControlProtocol.Build, engine.GetProperty("build").GetString());
        Assert.NotEqual(Environment.ProcessId, engine.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task A_replaced_Engine_keeps_its_Engine_Name_at_its_socket()
    {
        await using EngineSandbox sandbox = new();
        // The socket's file name differs from the Engine Name, so the new Engine's name can only come from the old one's hello.
        string socket = Path.Combine(sandbox.SkuaDir, "farm.sock");
        EngineEndpoint named = EngineEndpoint.Resolve("alt2", sandbox.SkuaDir, socket);
        try
        {
            await using (OtherVersionEngine other = new(sandbox, endpoint: named))
            {
                ProcessResult status = await sandbox.RunCliAsync(
                    new Dictionary<string, string> { [EngineEndpoint.SocketVariable] = socket }, "status", "--json");

                Assert.Equal(0, status.ExitCode);
                Assert.True(other.ShutdownRequested);
                Assert.StartsWith("skua: Replaced Engine 'alt2' from another build", status.Stderr);
                using JsonDocument json = JsonDocument.Parse(status.Stdout);
                JsonElement engine = json.RootElement.GetProperty("engine");
                Assert.Equal("alt2", engine.GetProperty("name").GetString());
                Assert.Equal(ControlProtocol.Build, engine.GetProperty("build").GetString());
            }
            Assert.True(EngineLock.IsHeld(named.LockPath));
            Assert.False(File.Exists(sandbox.Endpoint.LockPath));
        }
        finally
        {
            await EngineClient.StopAsync(named, EngineSandbox.StopTimeout);
        }
    }

    [Fact]
    public async Task Engine_stop_waits_for_the_lock_of_the_Engine_Name_the_Engine_gave()
    {
        await using EngineSandbox sandbox = new();
        string socket = Path.Combine(sandbox.SkuaDir, "farm.sock");
        EngineEndpoint named = EngineEndpoint.Resolve("alt2", sandbox.SkuaDir, socket);
        await using OtherVersionEngine other = new(sandbox, endpoint: named, lockHeldAfterShutdown: TimeSpan.FromSeconds(2));

        ProcessResult stop = await sandbox.RunCliAsync(
            new Dictionary<string, string> { [EngineEndpoint.SocketVariable] = socket }, "engine", "stop");

        Assert.Equal(0, stop.ExitCode);
        Assert.True(other.ShutdownRequested);
        Assert.False(EngineLock.IsHeld(named.LockPath));
    }

    [Fact]
    public async Task The_CLI_leaves_an_Engine_on_another_protocol_running_while_its_Script_runs_and_explains()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, scriptRunning: true);

        ProcessResult human = await sandbox.RunCliAsync("status");
        ProcessResult json = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.ScriptRunning), human.ExitCode);
        Assert.Contains("a Script is running in it", human.Stderr);
        Assert.Contains("'skua engine stop'", human.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.ScriptRunning), json.ExitCode);
        using (JsonDocument error = JsonDocument.Parse(json.Stdout))
            Assert.Equal("scriptRunning", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(other.ShutdownRequested);
        Assert.False(other.StatusCalled);
        Assert.True(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }

    [Fact]
    public async Task The_CLI_keeps_using_a_compatible_Engine_from_another_build_while_its_Script_runs()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, ControlProtocol.Version, scriptRunning: true);

        ProcessResult start = await sandbox.RunCliAsync("engine", "start", "--json");

        Assert.Equal(0, start.ExitCode);
        Assert.False(other.ShutdownRequested);
        string notice = Assert.Single(start.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("wasn't replaced, because a Script is running in it", notice);
        using JsonDocument json = JsonDocument.Parse(start.Stdout);
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task The_CLI_keeps_using_the_Skua_apps_Engine_from_another_build_and_never_asks_it_to_stop()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine app = new(sandbox, ControlProtocol.Version, host: EngineHost.App);

        ProcessResult start = await sandbox.RunCliAsync("engine", "start", "--json");

        Assert.Equal(0, start.ExitCode);
        Assert.False(app.ShutdownCalled);
        string notice = Assert.Single(start.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal($"skua: Engine 'default' from another build ({OtherVersionEngine.OtherBuild}, protocol {ControlProtocol.Version}) wasn't replaced: the Skua app hosts it; quit the app to replace it.", notice);
        Assert.Equal("", (await sandbox.RunCliAsync("engine", "start", "--json")).Stderr);
        using JsonDocument json = JsonDocument.Parse(start.Stdout);
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("app", json.RootElement.GetProperty("host").GetString());
    }

    [Fact]
    public async Task The_CLI_refuses_the_Skua_apps_Engine_on_another_protocol_with_a_quit_hint_and_never_asks_it_to_stop()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine app = new(sandbox, host: EngineHost.App);

        ProcessResult status = await sandbox.RunCliAsync("status");
        ProcessResult engine = await sandbox.RunCliAsync("engine", "status");

        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), status.ExitCode);
        Assert.Contains("The Skua app hosts it: quit the app, then try again.", status.Stderr);
        Assert.Equal(0, engine.ExitCode);
        Assert.Contains($"is running in the Skua app (pid {Environment.ProcessId}, build {OtherVersionEngine.OtherBuild}) on protocol {OtherVersionEngine.OtherProtocol}", engine.Stdout);
        Assert.Contains("; quit the app.", engine.Stdout);
        Assert.False(app.ShutdownCalled);
        Assert.False(app.StatusCalled);
    }

    [Fact]
    public async Task An_idle_Engine_from_another_build_is_stopped_and_a_new_one_started()
    {
        await using EngineSandbox sandbox = new();
        int stale;
        using (EngineConnection first = await sandbox.ConnectAsync())
            stale = first.Hello.Pid;
        List<string> notices = [];

        using EngineConnection connection = await EngineClient.ConnectAsync(Newer(sandbox, notices), Ct);

        Assert.NotEqual(stale, connection.Hello.Pid);
        Assert.StartsWith("Replaced Engine 'default' from another build", Assert.Single(notices));
    }

    [Fact]
    public async Task An_Engine_from_another_build_whose_Script_runs_is_kept_and_the_run_goes_on()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        List<string> notices = [];

        using EngineConnection connection = await EngineClient.ConnectAsync(Newer(sandbox, notices), Ct);
        ScriptStatusDto status = await connection.ScriptStatusAsync(Ct);

        Assert.Equal(game.Engine.Id, connection.Hello.Pid);
        Assert.Equal((ScriptState.Running, start.Run), (status.State, status.Run?.Number));
        Assert.Contains("wasn't replaced, because a Script is running in it", Assert.Single(notices));
    }

    [Fact]
    public async Task A_kept_Engine_from_another_build_is_noticed_once_per_Engine_and_build()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        List<string> notices = [];

        using (await EngineClient.ConnectAsync(Newer(sandbox, notices), Ct))
        using (await EngineClient.ConnectAsync(Newer(sandbox, notices), Ct))
            Assert.Contains("wasn't replaced, because a Script is running in it", Assert.Single(notices));
        using (await EngineClient.ConnectAsync(Newer(sandbox, notices) with { Build = "0.0.0-newest" }, Ct))
            Assert.Equal(2, notices.Count);
        using (await EngineClient.ConnectAsync(Newer(sandbox, notices), Ct))
            Assert.Equal(3, notices.Count);
    }

    [Fact]
    public async Task The_CLI_says_once_that_it_kept_an_Engine_from_another_build_and_status_and_engine_list_keep_showing_it()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, ControlProtocol.Version, scriptRunning: true, servesStatus: true);

        ProcessResult first = await sandbox.RunCliAsync("engine", "start", "--json");
        ProcessResult second = await sandbox.RunCliAsync("engine", "start", "--json");
        ProcessResult engineStatus = await sandbox.RunCliAsync("engine", "status");
        ProcessResult list = await sandbox.RunCliAsync("engine", "list");

        Assert.Equal((0, 0), (first.ExitCode, second.ExitCode));
        Assert.Contains("wasn't replaced, because a Script is running in it", Assert.Single(first.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("", second.Stderr);
        Assert.False(other.ShutdownRequested);
        Assert.Contains($"{OtherVersionEngine.OtherBuild}, another build than this skua's {ControlProtocol.Build}", engineStatus.Stdout);
        Assert.Contains($"{OtherVersionEngine.OtherBuild}, another build than this skua's {ControlProtocol.Build}", list.Stdout);
    }

    [Fact]
    public async Task Status_and_engine_list_of_an_Engine_kept_from_another_build_say_why_it_runs_on()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox, ControlProtocol.Version, scriptRunning: true, servesStatus: true);

        ProcessResult status = await sandbox.RunCliAsync("status");
        ProcessResult list = await sandbox.RunCliAsync("engine", "list");

        Assert.Equal((0, 0), (status.ExitCode, list.ExitCode));
        string note = $"another build than this skua's {ControlProtocol.Build}; a skua command replaces it once its Script ends";
        Assert.Contains(note, status.Stdout);
        Assert.Contains(note, list.Stdout);
    }

    /// <summary>The options of a client from a newer build, to which the Engines these tests start are from another build.</summary>
    private static EngineClientOptions Newer(EngineSandbox sandbox, List<string> notices) =>
        sandbox.ClientOptions with { ReplaceStale = true, Build = "0.0.0-newer", Notice = notices.Add };
}
