using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>script_start</c>, <c>script_stop</c>, <c>script_status</c> and <c>script_wait</c> against the fake Game Host.</summary>
public class ScriptRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Script_that_returns_completes_its_run_with_started_and_stopped_events()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Hello.cs", TestScripts.Main("""bot.Log("hello from the script");"""));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Hello.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptWaitReason.Ended, wait.Reason);
        Assert.Equal(ScriptState.Idle, wait.Status.State);
        Assert.Null(wait.Status.Run);
        ScriptRunResultDto last = wait.Status.LastRun!;
        Assert.Equal((start.Run, "Tests/Hello.cs", ScriptOutcome.Completed, (string?)null, 0), (last.Number, last.Script, last.Outcome, last.Error, last.Relogins));
        List<LogEntryDto> events = await ScriptEvents.AllAsync(game.Connection);
        Assert.Equal([EventTypes.ScriptStarted, EventTypes.ScriptStopped], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(start.Run, e.Run));
        Assert.False(events[0].Data!.Value.GetProperty("restart").GetBoolean());
        Assert.Equal("completed", events[1].Data!.Value.GetProperty("outcome").GetString());
        LogEntryDto line = (await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "hello from the script"))[0];
        Assert.Equal(start.Run, line.Run);
    }

    [Theory]
    [InlineData("""bot.StopAsync(true).GetAwaiter().GetResult(); bot.Log("after the stop");""")]
    [InlineData("""bot.Stop(); bot.Log("after the stop");""")]
    public async Task A_Script_that_stops_itself_ends_its_run_as_stopped(string body)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/SelfStop.cs", TestScripts.Main(body));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/SelfStop.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptWaitReason.Ended, wait.Reason);
        Assert.Equal((start.Run, ScriptOutcome.Stopped, (string?)null), (wait.Status.LastRun!.Number, wait.Status.LastRun.Outcome, wait.Status.LastRun.Error));
        LogEntryDto stopped = (await ScriptEvents.AllAsync(game.Connection)).Single(e => e.Type == EventTypes.ScriptStopped);
        Assert.Equal("stopped", ScriptEvents.Get(stopped, "outcome"));
    }

    [Fact]
    public async Task A_broken_Script_fails_with_CompileFailed_and_its_diagnostics_and_starts_no_run()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        string file = TestScripts.Write(sandbox, "Tests/Broken.cs", TestScripts.Main("""bot.Log("missing semicolon")"""));

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptStartAsync(file, cancellationToken: Ct));
        ScriptStatusDto status = await game.Connection.ScriptStatusAsync(Ct);

        Assert.Equal(ErrorCode.CompileFailed, e.Code);
        Assert.Contains(e.Diagnostics!, d => d.Contains("CS1002", StringComparison.Ordinal));
        Assert.Contains("CS1002", e.Message);
        Assert.Equal(new ScriptStatusDto(ScriptState.Idle, null, null), status);
        Assert.Empty(await ScriptEvents.AllAsync(game.Connection));
    }

    [Fact]
    public async Task A_Script_missing_from_the_Scripts_folder_fails_with_ScriptNotFound_suggesting_an_update()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptStartAsync("Farm/Nowhere.cs", cancellationToken: Ct));

        Assert.Equal(ErrorCode.ScriptNotFound, e.Code);
        Assert.Contains("skua scripts update", e.Message);
    }

    [Fact]
    public async Task Script_stop_ends_a_running_Script_cooperatively_and_turns_the_lag_killer_back_on()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);
        ScriptWaitResult running = await game.Connection.ScriptWaitAsync(0, Ct);
        await Task.Delay(300, Ct);
        ScriptStatusDto later = await game.Connection.ScriptStatusAsync(Ct);
        ScriptStopResult stop = await game.Connection.ScriptStopAsync(Ct);
        ScriptStopResult again = await game.Connection.ScriptStopAsync(Ct);

        Assert.Equal(ScriptWaitReason.Timeout, running.Reason);
        Assert.Equal(ScriptState.Running, running.Status.State);
        Assert.Equal((start.Run, "Tests/Loop.cs", 0, false, DialogMode.Ask, 120),
            (running.Status.Run!.Number, running.Status.Run.Script, running.Status.Run.Relogins, running.Status.Run.ReloggingIn, running.Status.Run.Dialogs, running.Status.Run.DialogTimeoutSec));
        // To a tenth of a second each, so 300 ms apart can read as little as 0.2 apart.
        Assert.InRange(Math.Round(later.Run!.ElapsedSec - running.Status.Run.ElapsedSec, 1), 0.2, 30);
        Assert.True(stop.WasRunning);
        Assert.True(stop.Ended);
        Assert.Equal(ScriptState.Idle, stop.Status.State);
        Assert.Equal(ScriptOutcome.Stopped, stop.Status.LastRun!.Outcome);
        Assert.Equal((false, true), (again.WasRunning, again.Ended));
        LogEntryDto stopped = (await ScriptEvents.AllAsync(game.Connection)).Single(e => e.Type == EventTypes.ScriptStopped);
        Assert.Equal("stopped", ScriptEvents.Get(stopped, "outcome"));
        Assert.Equal("killLag true", (await game.GameHost.CallsAsync()).Last(c => c.StartsWith("killLag", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task While_a_Script_runs_game_actions_are_refused_but_queries_and_eval_are_allowed()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);

        ErrorCode?[] refusals =
        [
            await CodeOf(() => game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct)),
            await CodeOf(() => game.Connection.LoginAsync("Galanoth", cancellationToken: Ct)),
            await CodeOf(() => game.Connection.LogoutAsync(Ct)),
            await CodeOf(() => game.Connection.JoinAsync("yulgar", cancellationToken: Ct)),
            await CodeOf(() => game.Connection.JumpAsync("Enter", cancellationToken: Ct)),
            await CodeOf(() => game.Connection.ScriptsUpdateAsync(Ct)),
            await CodeOf(() => game.Connection.ScriptOptionsAsync("Tests/Loop.cs", Ct)),
        ];
        StatusDto status = await game.Connection.StatusAsync(Ct);
        EvalResult eval = await game.Connection.EvalAsync("1 + 2", cancellationToken: Ct);

        Assert.All(refusals, code => Assert.Equal(ErrorCode.ScriptRunning, code));
        Assert.Equal(ScriptState.Running, status.Script.State);
        Assert.Equal(3, eval.Value!.Value.GetInt32());
    }

    [Fact]
    public async Task A_Script_that_throws_ends_with_an_error_outcome_and_a_script_error_event_with_its_stack()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Throws.cs", TestScripts.Main("""throw new System.InvalidOperationException("boom");"""));

        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Throws.cs", cancellationToken: Ct);
        ScriptWaitResult wait = await game.Connection.ScriptWaitAsync(60, Ct);

        Assert.Equal(ScriptOutcome.Error, wait.Status.LastRun!.Outcome);
        Assert.Equal("InvalidOperationException: boom", wait.Status.LastRun.Error);
        List<LogEntryDto> events = await ScriptEvents.AllAsync(game.Connection);
        Assert.Equal([EventTypes.ScriptStarted, EventTypes.ScriptError, EventTypes.ScriptStopped], events.Select(e => e.Type));
        Assert.Equal("InvalidOperationException: boom", ScriptEvents.Get(events[1], "error"));
        Assert.Contains("TestScript.ScriptMain", ScriptEvents.Get(events[1], "stack"));
        Assert.Equal(start.Run, events[1].Run);
        Assert.Equal("error", ScriptEvents.Get(events[2], "outcome"));
    }

    [Fact]
    public async Task A_Script_started_again_and_again_reports_each_run_in_order_however_short_it_is()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Throws.cs", TestScripts.Main("""throw new System.InvalidOperationException("boom");"""));

        for (int i = 0; i < 25; i++)
        {
            await game.Connection.ScriptStartAsync("Tests/Throws.cs", cancellationToken: Ct);
            await game.Connection.ScriptWaitAsync(60, Ct);
        }

        List<LogEntryDto> events = await ScriptEvents.AllAsync(game.Connection);
        Assert.All(events.GroupBy(e => e.Run), run =>
            Assert.Equal([EventTypes.ScriptStarted, EventTypes.ScriptError, EventTypes.ScriptStopped], run.Select(e => e.Type)));
        Assert.Equal(25, events.Select(e => e.Run).Distinct().Count());
    }

    [Fact]
    public async Task A_Script_that_ignores_its_stop_ends_its_run_as_stopTimedOut_and_the_Engine_stays_stopping_until_its_thread_ends()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        string release = Path.Combine(sandbox.SkuaDir, "release");
        TestScripts.Write(sandbox, "Tests/Stubborn.cs", TestScripts.Main($$"""
            while (!System.IO.File.Exists("{{release}}"))
            {
                try { Thread.Sleep(50); } catch (ThreadInterruptedException) { }
            }
            """));
        await game.Connection.ScriptStartAsync("Tests/Stubborn.cs", cancellationToken: Ct);

        ScriptStopResult stop = await game.Connection.ScriptStopAsync(Ct);
        ErrorCode? refused = await CodeOf(() => game.Connection.ScriptStartAsync("Tests/Stubborn.cs", cancellationToken: Ct));
        File.WriteAllText(release, "");
        await WaitForStateAsync(game.Connection, ScriptState.Idle);

        Assert.Equal((true, false), (stop.WasRunning, stop.Ended));
        Assert.Equal(ScriptState.Stopping, stop.Status.State);
        Assert.Null(stop.Status.Run);
        Assert.Equal(ScriptOutcome.StopTimedOut, stop.Status.LastRun!.Outcome);
        Assert.Equal(ErrorCode.ScriptRunning, refused);
        Assert.Equal([EventTypes.ScriptStarted, EventTypes.ScriptStopped], (await ScriptEvents.AllAsync(game.Connection)).Select(e => e.Type));
    }

    [Fact]
    public async Task A_restart_by_Cores_auto_relogin_is_the_same_run_with_a_relogin_counted()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Relogs.cs", TestScripts.Main("""
            bot.Options.SafeRelogin = false;
            bot.Options.ReloginTryDelay = 200;
            bot.Options.AutoRelogin = true;
            bot.Log("running");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Relogs.cs", cancellationToken: Ct);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "running");

        await game.GameHost.DoAsync("logout-button");
        await game.Connection.WaitForEventAsync(EventTypes.ScriptStarted, e => ScriptEvents.Get(e, "restart") == "True");
        ScriptStatusDto restarted = await game.Connection.ScriptStatusAsync(Ct);
        ScriptStopResult stop = await game.Connection.ScriptStopAsync(Ct);

        Assert.Equal(ScriptState.Running, restarted.State);
        Assert.Equal((start.Run, 1, false), (restarted.Run!.Number, restarted.Run.Relogins, restarted.Run.ReloggingIn));
        Assert.Null(restarted.LastRun);
        Assert.Equal((start.Run, ScriptOutcome.Stopped, 1), (stop.Status.LastRun!.Number, stop.Status.LastRun.Outcome, stop.Status.LastRun.Relogins));
        Assert.Equal(
            [$"{EventTypes.ScriptStarted} False", $"{EventTypes.ScriptStarted} True", $"{EventTypes.ScriptStopped} stopped"],
            (await ScriptEvents.AllAsync(game.Connection)).Select(e => $"{e.Type} {ScriptEvents.Get(e, "restart") ?? ScriptEvents.Get(e, "outcome")}"));
    }

    [Fact]
    public async Task A_lost_connection_seen_again_while_a_slow_Script_stops_is_still_one_relogin_and_a_later_one_relogins_again()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        // Core checks the connection message every 100ms; this Script takes about a second to stop.
        TestScripts.Write(sandbox, "Tests/SlowStop.cs", TestScripts.Main("""
            bot.Options.SafeRelogin = false;
            bot.Options.ReloginTryDelay = 200;
            bot.Options.AutoRelogin = true;
            bot.Log("running");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            Thread.Sleep(1000);
            """));
        await game.Connection.ScriptStartAsync("Tests/SlowStop.cs", cancellationToken: Ct);

        for (int disconnect = 1; disconnect <= 2; disconnect++)
        {
            await game.Connection.WaitForLogsAsync(LogKind.Script, disconnect, e => e.Text == "running");
            await game.GameHost.DoAsync("lose-connection Your connection to the server has been lost.");
            await game.Connection.WaitForLogsAsync(LogKind.Events, disconnect, e => e.Type == EventTypes.ScriptStarted && ScriptEvents.Get(e, "restart") == "True");
            await Task.Delay(1500, Ct);
        }
        await game.Connection.ScriptStopAsync(Ct);

        Assert.Equal(["triggered", "finished", "triggered", "finished"],
            (await GameEvents.AllAsync(game.Connection)).Where(e => e.Type == EventTypes.GameRelogin).Select(e => ScriptEvents.Get(e, "phase")));
    }

    [Fact]
    public async Task Stopping_a_run_while_Cores_auto_relogin_waits_to_restart_it_ends_the_run_and_the_restart_never_runs_the_Script()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Relogs.cs", TestScripts.Main("""
            bot.Options.SafeRelogin = false;
            bot.Options.ReloginTryDelay = 1500;
            bot.Options.AutoRelogin = true;
            bot.Log("running");
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Relogs.cs", cancellationToken: Ct);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "running");

        await game.GameHost.DoAsync("logout-button");
        await game.Connection.WaitForEventAsync(EventTypes.GameRelogin, e => ScriptEvents.Get(e, "phase") == "triggered");
        ScriptStatusDto relogging = await game.Connection.ScriptStatusAsync(Ct);
        ScriptStopResult stop = await game.Connection.ScriptStopAsync(Ct);
        await game.Connection.WaitForEventAsync(EventTypes.GameRelogin, e => ScriptEvents.Get(e, "phase") == "finished");
        await Task.Delay(1000, Ct);
        EvalResult coreRunning = await game.Connection.EvalAsync("Bot.Manager.ScriptRunning", cancellationToken: Ct);

        Assert.Equal((ScriptState.Running, true), (relogging.State, relogging.Run!.ReloggingIn));
        Assert.Equal((true, true), (stop.WasRunning, stop.Ended));
        Assert.Equal((start.Run, ScriptOutcome.Stopped, 0), (stop.Status.LastRun!.Number, stop.Status.LastRun.Outcome, stop.Status.LastRun.Relogins));
        Assert.Equal(ScriptState.Idle, (await game.Connection.ScriptStatusAsync(Ct)).State);
        Assert.False(coreRunning.Value!.Value.GetBoolean());
        Assert.Single(await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "running"));
    }

    [Fact]
    public async Task Stopping_the_Engine_stops_the_running_Script_first()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        TestScripts.Write(sandbox, "Tests/Loop.cs", TestScripts.Loop);
        await game.Connection.ScriptStartAsync("Tests/Loop.cs", cancellationToken: Ct);

        Assert.True(await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, Ct));

        string log = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(sandbox.Endpoint.LogFilesDir, "*.jsonl")), Ct);
        LogEntryDto stopped = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<LogEntryDto>(line, ControlJson.Options)!)
            .Single(e => e.Type == EventTypes.ScriptStopped);
        Assert.Equal("stopped", ScriptEvents.Get(stopped, "outcome"));
    }

    private static async Task WaitForStateAsync(EngineConnection connection, ScriptState state)
    {
        for (int i = 0; i < 200; i++)
        {
            if ((await connection.ScriptStatusAsync(Ct)).State == state)
                return;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"The Script state never became {state}.");
    }

    private static async Task<ErrorCode?> CodeOf<T>(Func<Task<T>> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (ControlException e)
        {
            return e.Code;
        }
    }
}

/// <summary>Writes Scripts for the Engine under test to compile.</summary>
public static class TestScripts
{
    /// <summary>A Script whose <c>ScriptMain(IScriptInterface bot)</c> runs <paramref name="body"/>.</summary>
    public static string Main(string body, string members = "") => $$"""
        using System.Threading;
        using Skua.Core.Interfaces;
        using Skua.Core.Options;
        using System.Collections.Generic;

        public class TestScript
        {
            {{members}}

            public void ScriptMain(IScriptInterface bot)
            {
                {{body}}
            }
        }
        """;

    /// <summary>A Script that runs until it is stopped.</summary>
    public static string Loop { get; } = Main("""
        while (!bot.ShouldExit)
            Thread.Sleep(50);
        """);

    /// <summary>Writes a Script at a path in the sandbox's Scripts folder, as <c>scripts_update</c> would, and returns its absolute path.</summary>
    public static string Write(EngineSandbox sandbox, string path, string source)
    {
        string file = Path.Combine(sandbox.SkuaDir, "Scripts", path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, source);
        return file;
    }
}

/// <summary>Reads the <c>script.*</c> events.</summary>
public static class ScriptEvents
{
    public static async Task<List<LogEntryDto>> AllAsync(EngineConnection connection) =>
        (await AllOfAsync(connection)).Where(e => e.Type!.StartsWith("script.", StringComparison.Ordinal)).ToList();

    /// <summary>Every event held, of any type.</summary>
    public static async Task<List<LogEntryDto>> AllOfAsync(EngineConnection connection)
    {
        List<LogEntryDto> events = [];
        string? cursor = null;
        while (true)
        {
            LogPage page = await connection.LogsAsync(LogKind.Events, cursor, 1000, TestContext.Current.CancellationToken);
            events.AddRange(page.Entries);
            cursor = page.Next;
            if (page.Entries.Count == 0)
                return events;
        }
    }

    public static string? Get(LogEntryDto entry, string property) =>
        entry.Data!.Value.TryGetProperty(property, out JsonElement value) ? value.ToString() : null;
}
