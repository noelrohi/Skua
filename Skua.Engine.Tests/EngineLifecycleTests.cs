using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

public class EngineLifecycleTests
{
    [Fact]
    public async Task First_status_auto_starts_an_Engine_and_returns_engine_fields()
    {
        await using EngineSandbox sandbox = new();

        using EngineConnection connection = await sandbox.ConnectAsync();
        StatusDto status = await connection.StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("default", status.Engine.Name);
        Assert.Equal(ControlProtocol.Version, status.Engine.Protocol);
        Assert.False(string.IsNullOrEmpty(status.Engine.Build));
        Assert.True(status.Engine.Pid > 0);
        Assert.True(status.Game.GameHostUp);
        Assert.True(File.Exists(sandbox.Endpoint.SocketPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sandbox.Endpoint.SocketPath));
    }

    [Fact]
    public async Task An_auto_started_Engine_outlives_its_connection()
    {
        await using EngineSandbox sandbox = new();

        int pid;
        using (EngineConnection first = await sandbox.ConnectAsync())
            pid = first.Hello.Pid;
        using EngineConnection second = await sandbox.ConnectAsync();

        Assert.Equal(pid, second.Hello.Pid);
    }

    [Fact]
    public async Task Engine_stop_removes_the_socket_and_releases_the_lock()
    {
        await using EngineSandbox sandbox = new();
        using (await sandbox.ConnectAsync())
        {
        }

        bool stopped = await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken);

        Assert.True(stopped);
        Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
        Assert.False(await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_Engine_on_the_same_name_is_refused()
    {
        await using EngineSandbox sandbox = new();
        using EngineConnection connection = await sandbox.ConnectAsync();

        Process second = sandbox.StartEngineProcess();
        await second.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, second.ExitCode);
        Assert.Contains("already running", await second.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        StatusDto status = await connection.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(connection.Hello.Pid, status.Engine.Pid);
    }

    [Fact]
    public async Task A_stale_socket_is_cleaned_up_and_an_Engine_auto_starts()
    {
        await using EngineSandbox sandbox = new();
        (Process crashed, EngineConnection connection) = await sandbox.StartEngineAsync();
        connection.Dispose();
        crashed.Kill();
        await crashed.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(sandbox.Endpoint.SocketPath));

        using EngineConnection restarted = await sandbox.ConnectAsync();

        Assert.NotEqual(crashed.Id, restarted.Hello.Pid);
        StatusDto status = await restarted.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(restarted.Hello.Pid, status.Engine.Pid);
    }

    [Fact]
    public async Task A_held_lock_without_an_answering_socket_is_reported_as_starting_or_hung()
    {
        await using EngineSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Endpoint.EnginesDir);
        using EngineLock hung = EngineLock.TryAcquire(sandbox.Endpoint.LockPath)!;

        ControlException error = await Assert.ThrowsAsync<ControlException>(() =>
            EngineClient.ConnectAsync(sandbox.ClientOptions with { StartTimeout = TimeSpan.FromMilliseconds(500) }, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.EngineUnavailable, error.Code);
        Assert.Contains("starting or hung", error.Message);
        Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
    }

    [Fact]
    public async Task Concurrent_auto_starts_share_one_Engine()
    {
        await using EngineSandbox sandbox = new();

        EngineConnection[] connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sandbox.ConnectAsync()));

        Assert.Single(connections.Select(c => c.Hello.Pid).Distinct());
        foreach (EngineConnection connection in connections)
            connection.Dispose();
    }

    [Theory]
    [InlineData(SIGTERM)]
    [InlineData(SIGINT)]
    public async Task A_signal_shuts_the_Engine_down_cleanly(int signal)
    {
        await using EngineSandbox sandbox = new();
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync();
        connection.Dispose();

        Assert.Equal(0, kill(engine.Id, signal));
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, engine.ExitCode);
        Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }

    private const int SIGINT = 2;
    private const int SIGTERM = 15;

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
