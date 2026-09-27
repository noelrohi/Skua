using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

public class GameHostTests
{
    [Fact]
    public async Task Status_reports_the_Game_Host_up_while_it_runs()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Send('X', "loaded").Log(2, "wgpu: fake");
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
    public async Task A_first_start_on_an_empty_data_folder_succeeds()
    {
        await using EngineSandbox sandbox = new();
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.SkuaDir));

        (_, EngineConnection connection) = await sandbox.StartEngineAsync();
        using (connection)
        {
            StatusDto status = await connection.StatusAsync(TestContext.Current.CancellationToken);

            Assert.True(status.Game.GameHostUp);
        }
    }

    [Fact]
    public async Task The_Engine_loads_the_game_when_the_Game_Client_asks()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).LogCalls().Send('E', """<invoke name="requestLoadGame" returntype="xml"><arguments></arguments></invoke>""");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        connection.Dispose();

        string[] calls = await WaitForAsync(gameHost.CallsAsync, c => c.Contains("loadClient"));

        Assert.Contains("loadClient", calls);
    }

    [Fact]
    public async Task The_Engine_logs_how_many_callbacks_the_loaded_Game_Client_registered()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox)
            .Send('X', "isNull").Send('X', "getGameObject").Send('X', "loadClient")
            .Send('E', """<invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""")
            .LogCalls().Reply("isNull", "<true/>");
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        connection.Dispose();

        string log = await StopAndReadLogAsync(sandbox, engine);

        Assert.Contains("3 callbacks registered", log);
    }

    [Fact]
    public async Task A_Game_Host_exit_is_a_gamehost_exited_event()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Sleep(200).Exit(3);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await WaitForStatusAsync(connection, s => !s.Game.GameHostUp);

            List<LogEntryDto> exited = await LogTests.WaitForAsync(connection, LogKind.Events, 1, e => e.Type == EventTypes.GameHostExited);

            Assert.Equal(3, exited.Single().Data!.Value.GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task Killing_the_Engine_ends_the_Game_Host_within_a_second()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new(sandbox);
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        connection.Dispose();
        int pid = await gameHost.PidAsync();

        engine.Kill();
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);
        Stopwatch sinceKill = Stopwatch.StartNew();
        await WaitForExitAsync(pid);

        Assert.False(IsRunning(pid));
        Assert.True(sinceKill.Elapsed < TimeSpan.FromSeconds(1), $"The Game Host took {sinceKill.Elapsed.TotalMilliseconds:0} ms to exit.");
    }

    [Fact]
    public async Task A_missing_Game_Client_fails_the_start_naming_the_path()
    {
        await using EngineSandbox sandbox = new();
        string missing = Path.Combine(sandbox.SkuaDir, "no-such.swf");

        Process engine = sandbox.StartEngineProcess(new Dictionary<string, string> { ["SKUA_SWF"] = missing });
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineExitCodes.GameHostMissing, engine.ExitCode);
        Assert.Contains(missing, await engine.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
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

    private static async Task<string> StopAndReadLogAsync(EngineSandbox sandbox, Process engine)
    {
        await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, TestContext.Current.CancellationToken);
        await engine.WaitForExitAsync(TestContext.Current.CancellationToken);
        return await engine.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            T value = await read();
            if (condition(value) || waited.Elapsed > TimeSpan.FromSeconds(10))
                return value;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
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
