using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

public class GameHostTests
{
    [Fact]
    public async Task Status_reports_the_Game_Host_up_while_it_runs()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Send('X', "loaded").Send('L', "WARN wgpu: fake");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await gameHost.PidAsync();

            StatusDto status = await connection.StatusAsync(TestContext.Current.CancellationToken);

            Assert.True(status.Game.GameHostUp);
            Assert.Null(status.Game.State);
        }
    }

    [Fact]
    public async Task Status_reports_the_Game_Host_down_after_it_exits()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Sleep(200).Exit(1);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await WaitForExitAsync(await gameHost.PidAsync());

            StatusDto status = await WaitForStatusAsync(connection, s => !s.Game.GameHostUp);

            Assert.Equal(GameState.NotStarted, status.Game.State);
        }
    }

    [Fact]
    public async Task Engine_stop_closes_the_Game_Host()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new(sandbox);
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        connection.Dispose();
        int pid = await gameHost.PidAsync();

        Assert.True(await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken));

        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.ExitCode == 0, await engine.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task A_missing_Game_Host_fails_the_start_naming_the_path()
    {
        await using EngineSandbox sandbox = new();
        string missing = Path.Combine(sandbox.SkuaDir, "no-such-gamehost");

        Process engine = sandbox.StartEngineProcess(new Dictionary<string, string> { ["SKUA_GAMEHOST"] = missing });
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineExitCodes.GameHostMissing, engine.ExitCode);
        Assert.Contains(missing, await engine.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
    }

    private static async Task<StatusDto> WaitForStatusAsync(EngineConnection connection, Func<StatusDto, bool> condition)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            StatusDto status = await connection.StatusAsync(TestContext.Current.CancellationToken);
            if (condition(status) || waited.Elapsed > TimeSpan.FromSeconds(10))
                return status;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task WaitForExitAsync(int pid)
    {
        for (int i = 0; i < 200 && IsRunning(pid); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
