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

    /// <summary>The options of a client from a newer build, to which the Engines these tests start are from another build.</summary>
    private static EngineClientOptions Newer(EngineSandbox sandbox, List<string> notices) =>
        sandbox.ClientOptions with { ReplaceStale = true, Build = "0.0.0-newer", Notice = notices.Add };
}
