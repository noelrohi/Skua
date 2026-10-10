using System.Diagnostics;
using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// The Hook Runner, <c>skua hooks</c> (#170): it follows fake Engines' events and runs the Hooks in the sandbox's <c>hooks</c> folder; and the
/// real Engine's <c>hook_ran</c>, which records each run.
/// </summary>
public class HookTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_event_runs_its_hook_with_the_event_on_stdin_and_the_Engine_in_the_environment()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        WriteHook(sandbox, EventTypes.PlayerDeath, """
            #!/bin/sh
            cat > "$SKUA_DIR/stdin.json"
            printf '%s\n%s\n%s\n' "$SKUA_ENGINE_NAME" "$SKUA_ENGINE_SOCKET" "$SKUA_DIR" > "$SKUA_DIR/env.txt"
            echo "farm died"
            echo "to stderr" >&2
            exit 3
            """);
        Process runner = sandbox.StartCli("hooks");
        await farm.Subscribed.WaitAsync(Wait, Ct);

        long seq = farm.Emit(EventTypes.PlayerDeath, new { map = "battleon", cell = "Enter" });
        HookRunDto run = await farm.NextRunAsync(Wait);

        Assert.Equal(EventTypes.PlayerDeath, run.Hook);
        Assert.Equal(seq, run.EventSeq);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("farm died", run.Output);
        Assert.Contains("to stderr", run.Output);
        Assert.True(run.DurationMs >= 0);
        Assert.InRange(run.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using JsonDocument stdin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sandbox.SkuaDir, "stdin.json"), Ct));
        Assert.Equal(seq, stdin.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(EventTypes.PlayerDeath, stdin.RootElement.GetProperty("type").GetString());
        Assert.Equal("battleon", stdin.RootElement.GetProperty("data").GetProperty("map").GetString());
        Assert.Equal(
            ["farm", farm.Endpoint.SocketPath, sandbox.SkuaDir],
            (await File.ReadAllLinesAsync(Path.Combine(sandbox.SkuaDir, "env.txt"), Ct)).ToArray());
        Assert.False(runner.HasExited);
    }

    [Fact]
    public async Task An_event_with_no_hook_or_one_that_isnt_executable_runs_nothing()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        WriteHook(sandbox, EventTypes.GameState, "#!/bin/sh\necho ran\n", executable: false);
        WriteHook(sandbox, EventTypes.PlayerDeath, "#!/bin/sh\necho ran\n");
        sandbox.StartCli("hooks");
        await farm.Subscribed.WaitAsync(Wait, Ct);

        farm.Emit(EventTypes.MapJoined, new { map = "battleon" });
        farm.Emit(EventTypes.GameState, new { from = "loginScreen", to = "playing" });
        farm.Emit(EventTypes.PlayerDeath, new { map = "battleon" });
        HookRunDto run = await farm.NextRunAsync(Wait);
        await Task.Delay(500, Ct);

        Assert.Equal(EventTypes.PlayerDeath, run.Hook);
        Assert.False(farm.HasRun);
    }

    [Fact]
    public async Task A_hook_that_hangs_doesnt_hold_up_the_next_event()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        WriteHook(sandbox, EventTypes.PlayerAfk, "#!/bin/sh\nexec sleep 120\n");
        WriteHook(sandbox, EventTypes.PlayerDeath, "#!/bin/sh\necho ran\n");
        sandbox.StartCli("hooks");
        await farm.Subscribed.WaitAsync(Wait, Ct);

        farm.Emit(EventTypes.PlayerAfk, new { });
        farm.Emit(EventTypes.PlayerDeath, new { map = "battleon" });
        HookRunDto run = await farm.NextRunAsync(Wait);

        Assert.Equal(EventTypes.PlayerDeath, run.Hook);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task A_second_runner_for_the_same_data_folder_refuses()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        sandbox.StartCli("hooks");
        await farm.Subscribed.WaitAsync(Wait, Ct);

        ProcessResult second = await sandbox.RunCliAsync("hooks");

        Assert.Equal(ExitCodes.Failure, second.ExitCode);
        Assert.Contains("already runs", second.Stderr);
    }

    [Fact]
    public async Task The_runner_follows_by_the_push_op_and_never_polls()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        WriteHook(sandbox, EventTypes.PlayerDeath, "#!/bin/sh\n");
        sandbox.StartCli("hooks");
        await farm.Subscribed.WaitAsync(Wait, Ct);

        farm.Emit(EventTypes.PlayerDeath, new { map = "battleon" });
        await farm.NextRunAsync(Wait);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        // One logs call reads where the events end, so an Engine already running when the runner starts runs no hook for its past.
        Assert.Equal(["hello", "logs", "subscribe", "hook_ran"], farm.Calls.ToArray());
        Assert.Equal([LogKind.Events], farm.Subscription!.Value.Kinds);
        Assert.NotNull(farm.Subscription.Value.After);
    }

    [Fact]
    public async Task An_Engine_that_starts_later_is_followed_from_its_start_and_one_that_restarts_is_followed_again()
    {
        await using EngineSandbox sandbox = new();
        WriteHook(sandbox, EventTypes.PlayerDeath, "#!/bin/sh\n");
        sandbox.StartCli("hooks");
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        await using (FakeEngine farm = new(sandbox, "farm"))
        {
            await farm.Subscribed.WaitAsync(Wait, Ct);
            farm.Emit(EventTypes.PlayerDeath, new { map = "battleon" });

            Assert.Equal(EventTypes.PlayerDeath, (await farm.NextRunAsync(Wait)).Hook);
            Assert.Null(farm.Subscription!.Value.After);
            Assert.DoesNotContain("logs", farm.Calls);
        }

        await using FakeEngine again = new(sandbox, "farm");
        await again.Subscribed.WaitAsync(Wait, Ct);
        again.Emit(EventTypes.PlayerDeath, new { map = "yulgar" });

        Assert.Equal(EventTypes.PlayerDeath, (await again.NextRunAsync(Wait)).Hook);
    }

    [Fact]
    public async Task With_engine_the_runner_follows_only_that_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using FakeEngine farm = new(sandbox, "farm");
        await using FakeEngine butler = new(sandbox, "butler");
        WriteHook(sandbox, EventTypes.PlayerDeath, "#!/bin/sh\n");
        sandbox.StartCli("hooks", "--engine", "butler");
        await butler.Subscribed.WaitAsync(Wait, Ct);

        farm.Emit(EventTypes.PlayerDeath, new { map = "battleon" });
        butler.Emit(EventTypes.PlayerDeath, new { map = "yulgar" });
        await butler.NextRunAsync(Wait);
        await Task.Delay(500, Ct);

        Assert.Empty(farm.Calls);
        Assert.False(farm.HasRun);
    }

    [Fact]
    public async Task Hook_ran_records_a_hook_ran_event_that_skua_logs_reads()
    {
        await using EngineSandbox sandbox = new();
        using EngineConnection connection = await sandbox.ConnectAsync();

        await connection.HookRanAsync(new HookRunDto(EventTypes.InventoryFull, 7, 1_700_000_000_000, 1234, 0, "banked\n"), Ct);
        ControlException blank = await Assert.ThrowsAsync<ControlException>(
            () => connection.HookRanAsync(new HookRunDto(" ", 7, 1_700_000_000_000, 1, 0, ""), Ct));
        LogPage events = await connection.LogsAsync(LogKind.Events, cancellationToken: Ct);
        ProcessResult logs = await sandbox.RunCliAsync("logs", "events");

        LogEntryDto ran = Assert.Single(events.Entries, e => e.Type == EventTypes.HookRan);
        HookRunDto run = ran.Data!.Value.Deserialize<HookRunDto>(ControlJson.Options)!;
        Assert.Equal(new HookRunDto(EventTypes.InventoryFull, 7, 1_700_000_000_000, 1234, 0, "banked\n"), run);
        Assert.Equal(ErrorCode.InvalidArgument, blank.Code);
        Assert.Contains("hook.ran", logs.Stdout);
    }

    [Fact]
    public async Task The_example_relogin_hook_logs_an_account_back_in_after_an_unexpected_logout_and_never_after_skua_logout()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        WriteHook(sandbox, EventTypes.GameDisconnected, await File.ReadAllTextAsync(Path.Combine(EngineSandbox.BinDir, "hooks", EventTypes.GameDisconnected), Ct));
        // The Hook runs the skua on PATH, as an installed one does.
        Process runner = sandbox.StartCli(
            new Dictionary<string, string> { ["PATH"] = $"{EngineSandbox.BinDir}:{Environment.GetEnvironmentVariable("PATH")}" }, "hooks");
        await WaitForLineAsync(runner, "Following Engine 'default'");

        await game.GameHost.DoAsync("idle-logout");
        HookRunDto relogin = Run(await game.Connection.WaitForEventAsync(EventTypes.HookRan));
        StatusDto back = await game.Connection.WaitForStatusAsync(status => status.Game.State == GameState.Playing);
        await game.Connection.LogoutAsync(Ct);
        LogEntryDto logoutRun = await game.Connection.WaitForEventAsync(EventTypes.HookRan, e => Run(e).EventSeq > relogin.EventSeq);
        await Task.Delay(1000, Ct);

        Assert.Equal(0, relogin.ExitCode);
        Assert.Contains("logging test back in", relogin.Output);
        Assert.Contains("Logged in as SkuaTester (the Test Account) on Galanoth.", relogin.Output);
        Assert.Equal("Galanoth", back.Game.Server);
        Assert.Equal(0, Run(logoutRun).ExitCode);
        Assert.Contains("logged out deliberately", Run(logoutRun).Output);
        Assert.Equal(GameState.LoginScreen, (await game.Connection.StatusAsync(Ct)).Game.State);
    }

    /// <summary>A full Misc Space is the Engine's <c>misc.full</c> (#251), so a Hook of that name runs on it with the drop it has no room for.</summary>
    [Fact]
    public async Task A_misc_full_hook_runs_when_a_misc_item_drops_with_no_room_in_Misc_Space()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        WriteHook(sandbox, EventTypes.MiscFull, "#!/bin/sh\ncat\n");
        Process runner = sandbox.StartCli("hooks");
        await WaitForLineAsync(runner, "Following Engine 'default'");

        // Treasure Chest fills the one Misc Space slot.
        await game.GameHost.DoAsync("misc-space 1");
        await game.GameHost.DoAsync("drop 41 1 Gem");
        HookRunDto run = Run(await game.Connection.WaitForEventAsync(EventTypes.HookRan));

        Assert.Equal((EventTypes.MiscFull, (int?)0), (run.Hook, run.ExitCode));
        using JsonDocument stdin = JsonDocument.Parse(run.Output);
        JsonElement data = stdin.RootElement.GetProperty("data");
        Assert.Equal((1, 1, 41, "Gem"), (data.GetProperty("used").GetInt32(), data.GetProperty("slots").GetInt32(),
            data.GetProperty("drop").GetProperty("id").GetInt32(), data.GetProperty("drop").GetProperty("name").GetString()));
    }

    private static HookRunDto Run(LogEntryDto entry) => entry.Data!.Value.Deserialize<HookRunDto>(ControlJson.Options)!;

    private static async Task WaitForLineAsync(Process process, string text)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Wait);
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Contains(text))
            {
                // Keep reading, so the runner never blocks on a full pipe.
                _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                return;
            }
        }
        throw new InvalidOperationException($"skua hooks exited before saying '{text}'.");
    }

    private static void WriteHook(EngineSandbox sandbox, string name, string script, bool executable = true)
    {
        string hooks = Directory.CreateDirectory(Path.Combine(sandbox.SkuaDir, "hooks")).FullName;
        string path = Path.Combine(hooks, name);
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(path, executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
