using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Avalonia.Tests;

/// <summary>The Engine the Mac App hosts serves the normal socket and takes the host's services, and no Control Surface can stop it.</summary>
/// <remarks>In the Game View tests' collection, as one of those restarts the Game Host.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class AppEngineTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Skua_status_connects_to_the_app_hosted_Engine_and_says_the_app_hosts_it()
    {
        ProcessResult json = await RunCliAsync("status", "--json");
        ProcessResult human = await RunCliAsync("status");
        using EngineConnection connection = (await EngineClient.TryConnectAsync(app.Engine.Endpoint, Ct))!;

        Assert.True(json.ExitCode == 0, $"skua status exited with {json.ExitCode}: {json.Stderr}");
        using JsonDocument status = JsonDocument.Parse(json.Stdout);
        JsonElement engine = status.RootElement.GetProperty("engine");
        Assert.Equal(EngineName.Default, engine.GetProperty("name").GetString());
        Assert.Equal(Environment.ProcessId, engine.GetProperty("pid").GetInt32());
        Assert.Equal(ControlProtocol.Build, engine.GetProperty("build").GetString());
        Assert.Equal("app", engine.GetProperty("host").GetString());
        Assert.True(status.RootElement.GetProperty("game").GetProperty("gameHostUp").GetBoolean());
        Assert.Contains($"Engine  default (in the Skua app, pid {Environment.ProcessId}, ", human.Stdout);
        Assert.Equal(EngineHost.App, connection.Hello.Host);
    }

    [Fact]
    public async Task Skua_engine_stop_exits_with_the_EngineOwnedByApp_code_and_leaves_the_game_running()
    {
        ProcessResult stop = await RunCliAsync("engine", "stop");
        ProcessResult json = await RunCliAsync("engine", "stop", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.EngineOwnedByApp), stop.ExitCode);
        Assert.Equal("skua: The Skua app owns Engine 'default'; quit the app to stop it.", stop.Stderr.Trim());
        Assert.Equal(ExitCodes.For(ErrorCode.EngineOwnedByApp), json.ExitCode);
        using (JsonDocument error = JsonDocument.Parse(json.Stdout))
            Assert.Equal("engineOwnedByApp", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        await Task.Delay(200, Ct);
        Assert.False(app.Engine.Completion.IsCompleted);
        Assert.True(app.Flash.IsGameHostRunning);
        Assert.True(File.Exists(app.Engine.Endpoint.SocketPath));
        Assert.True(EngineLock.IsHeld(app.Engine.Endpoint.LockPath));
        StatusDto status = await app.Engine.Rpc.StatusAsync(Ct);
        Assert.True(status.Game.GameHostUp);
    }

    [Fact]
    public async Task Shutdown_and_shutdown_if_idle_are_refused_over_the_socket()
    {
        using EngineConnection connection = (await EngineClient.TryConnectAsync(app.Engine.Endpoint, Ct))!;

        ControlException shutdown = await Assert.ThrowsAsync<ControlException>(() => connection.ShutdownAsync(Ct));
        ControlException ifIdle = await Assert.ThrowsAsync<ControlException>(() => connection.ShutdownIfIdleAsync(Ct));

        Assert.Equal(ErrorCode.EngineOwnedByApp, shutdown.Code);
        Assert.Equal("The Skua app owns Engine 'default'; quit the app to stop it.", shutdown.Message);
        Assert.Equal(ErrorCode.EngineOwnedByApp, ifIdle.Code);
        Assert.False(app.Engine.Completion.IsCompleted);
        // Refused before it takes the Engine: commands still run.
        Assert.NotNull(await connection.ServersAsync(Ct));
    }

    [Fact]
    public async Task A_client_from_another_build_keeps_using_the_apps_Engine_and_never_replaces_it()
    {
        List<string> notices = [];
        EngineClientOptions other = new()
        {
            Endpoint = app.Engine.Endpoint,
            EngineExecutable = "/usr/bin/false",
            Build = "0.0.0-other",
            ReplaceStale = true,
            Notice = notices.Add,
        };

        using EngineConnection connection = await EngineClient.ConnectAsync(other, Ct);

        Assert.Equal(Environment.ProcessId, connection.Hello.Pid);
        Assert.Equal($"Engine 'default' from another build ({ControlProtocol.Build}, protocol {ControlProtocol.Version}) wasn't replaced: the Skua app hosts it; quit the app to replace it.", Assert.Single(notices));
        Assert.False(app.Engine.Completion.IsCompleted);
        Assert.Equal(EngineHost.App, (await connection.StatusAsync(Ct)).Engine.Host);
    }

    [Fact]
    public void The_host_adds_its_services_to_the_container_Ioc_Default_serves()
    {
        Assert.NotNull(Ioc.Default.GetService<HostMarker>());
        Assert.Same(app.Engine.Services.GetService(typeof(HostMarker)), Ioc.Default.GetService<HostMarker>());
    }

    /// <summary>Runs <c>skua</c> against the app's Engine, never auto-starting another.</summary>
    private static async Task<ProcessResult> RunCliAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "skua"), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = AppEngine.SkuaDir;
        startInfo.Environment[EngineClientOptions.EngineExecutableVariable] = "/usr/bin/false";
        using Process cli = Process.Start(startInfo)!;
        Task<string> stdout = cli.StandardOutput.ReadToEndAsync(Ct);
        Task<string> stderr = cli.StandardError.ReadToEndAsync(Ct);
        await cli.WaitForExitAsync(Ct);
        return new ProcessResult(cli.ExitCode, await stdout, await stderr);
    }
}
