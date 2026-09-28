using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Control;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The take-over offer, against a real <c>skua-engine</c> running the simulated game in a sandbox of its own, or a stand-in Engine from
/// another build. The app's Engine already runs in this process, so the start the take-over ends with only records that it happened.
/// </summary>
/// <remarks>In the Game View tests' collection, as some of those change the environment the headless Engines here start with.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class TakeOverTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Shows_what_the_headless_Engine_is_doing_then_Take_over_stops_it_and_starts_the_apps()
    {
        await using EngineSandbox sandbox = new();
        (Process engine, EngineConnection connection) = await StartHeadlessAsync(sandbox);
        using (connection)
            await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await using Shown shown = Show(sandbox);

        await shown.LookAsync();

        Assert.Equal(TakeOverStep.Offer, shown.Model.Step);
        Assert.Contains($"Engine 'default' is already running in skua-engine (pid {engine.Id})", shown.View.MessageText.Text);
        Assert.Equal($"Account: {AppEngine.Keychain.Username}, level 10, on Galanoth\nMap: battleon, Enter\nScript: none running", shown.View.DetailsText.Text);
        Assert.True(shown.View.TakeOverButton.IsVisible);
        Assert.Equal("Take over", shown.View.TakeOverButton.Content);

        Click(shown.Window, shown.View.TakeOverButton);
        await PumpUntilAsync(() => shown.Model.Step is TakeOverStep.Done or TakeOverStep.Stuck, "the take-over");

        Assert.Equal(TakeOverStep.Done, shown.Model.Step);
        Assert.True(engine.HasExited, "the headless Engine exited");
        Assert.Equal(0, engine.ExitCode);
        Assert.Equal([(LockHeld: false, SocketExists: false)], shown.Starts);
    }

    [AvaloniaFact]
    public async Task A_running_Script_makes_Take_over_ask_a_second_time_before_it_stops_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        (Process engine, EngineConnection connection) = await StartHeadlessAsync(sandbox);
        string script = WriteLoopScript(sandbox);
        using (connection)
            await connection.ScriptStartAsync(script, cancellationToken: Ct);
        await using Shown shown = Show(sandbox);

        await shown.LookAsync();
        // A Script in the Scripts folder goes by its path there.
        Assert.Equal("Account: not logged in\nScript: Tests/Loop.cs is running", shown.View.DetailsText.Text);
        Click(shown.Window, shown.View.TakeOverButton);
        await PumpUntilAsync(() => shown.Model.Step != TakeOverStep.TakingOver, "the first Take over");

        Assert.Equal(TakeOverStep.Confirm, shown.Model.Step);
        Assert.Equal("A Script is running in Engine 'default'. Taking over stops the Script, and closes the game it plays.", shown.View.MessageText.Text);
        Assert.Equal("Stop it and take over", shown.View.TakeOverButton.Content);
        Assert.False(engine.HasExited, "the first Take over leaves a running Script alone");
        Assert.Empty(shown.Starts);
        using (EngineConnection check = (await EngineClient.TryConnectAsync(sandbox.Endpoint, Ct))!)
            Assert.Equal(ScriptState.Running, (await check.ScriptStatusAsync(Ct)).State);

        Click(shown.Window, shown.View.TakeOverButton);
        await PumpUntilAsync(() => shown.Model.Step is TakeOverStep.Done or TakeOverStep.Stuck, "the second Take over");

        Assert.Equal(TakeOverStep.Done, shown.Model.Step);
        Assert.True(engine.HasExited, "the headless Engine exited");
        Assert.Equal([(LockHeld: false, SocketExists: false)], shown.Starts);
    }

    [AvaloniaFact]
    public async Task Quit_leaves_the_headless_Engine_running()
    {
        await using EngineSandbox sandbox = new();
        (Process engine, EngineConnection connection) = await StartHeadlessAsync(sandbox);
        connection.Dispose();
        await using Shown shown = Show(sandbox);
        await shown.LookAsync();

        Click(shown.Window, shown.View.QuitButton);

        Assert.True(shown.QuitRequested);
        Assert.Empty(shown.Starts);
        Assert.False(engine.HasExited);
    }

    [AvaloniaFact]
    public async Task An_Engine_from_another_build_gets_the_same_offer_and_Take_over_still_stops_it()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);
        await using Shown shown = Show(sandbox);

        await shown.LookAsync();

        Assert.Equal(TakeOverStep.Offer, shown.Model.Step);
        Assert.Equal($"It is from another build ({OtherVersionEngine.OtherBuild}, protocol {OtherVersionEngine.OtherProtocol}), so what it is doing can't be read.", shown.View.DetailsText.Text);
        Assert.False(other.StatusCalled);

        Click(shown.Window, shown.View.TakeOverButton);
        await PumpUntilAsync(() => shown.Model.Step is TakeOverStep.Done or TakeOverStep.Stuck, "the take-over");

        Assert.Equal(TakeOverStep.Done, shown.Model.Step);
        Assert.True(other.ShutdownRequested);
        Assert.Equal([(LockHeld: false, SocketExists: false)], shown.Starts);
    }

    [AvaloniaFact]
    public async Task Another_Skua_apps_Engine_cant_be_taken_over()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine app = new(sandbox, ControlProtocol.Version, host: EngineHost.App);
        await using Shown shown = Show(sandbox);

        await shown.LookAsync();

        Assert.Equal(TakeOverStep.Stuck, shown.Model.Step);
        Assert.StartsWith($"Another Skua app (pid {Environment.ProcessId}) runs Engine 'default'", shown.View.MessageText.Text);
        Assert.False(shown.View.TakeOverButton.IsVisible);
        Assert.True(shown.View.TryAgainButton.IsVisible);
        Assert.False(app.ShutdownCalled);
        Assert.False(app.StatusCalled);
    }

    [AvaloniaFact]
    public async Task An_Engine_that_holds_the_lock_but_doesnt_answer_is_reported_as_starting_or_hung()
    {
        await using EngineSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Endpoint.EnginesDir);
        using (EngineLock.TryAcquire(sandbox.Endpoint.LockPath))
        {
            await using Shown shown = Show(sandbox);

            await shown.LookAsync();

            Assert.Equal(TakeOverStep.Stuck, shown.Model.Step);
            Assert.Contains("it is starting or hung", shown.View.MessageText.Text);
            Assert.True(shown.View.TryAgainButton.IsVisible);
            Assert.Empty(shown.Starts);
        }
    }

    [AvaloniaFact]
    public async Task When_the_Engine_has_gone_by_the_time_the_app_looks_the_app_starts_its_own_at_once()
    {
        await using EngineSandbox sandbox = new();
        await using Shown shown = Show(sandbox);

        await shown.LookAsync();

        Assert.Equal(TakeOverStep.Done, shown.Model.Step);
        Assert.Single(shown.Starts);
    }

    /// <summary>
    /// Starts <c>skua-engine</c> in the sandbox with the simulated game of its own, which accepts the fake Keychain's Test Account, and waits
    /// for the login screen.
    /// </summary>
    private static async Task<(Process Engine, EngineConnection Connection)> StartHeadlessAsync(EngineSandbox sandbox)
    {
        string scenario = Path.Combine(sandbox.SkuaDir, "fake-gamehost.scenario");
        File.WriteAllLines(scenario,
        [
            $"game {AppEngine.Keychain.Username} {AppEngine.Keychain.Password}",
            $"servers {FakeServer.ListJson(AppEngine.Servers)}",
            """send E <invoke name="loaded" returntype="xml"><arguments></arguments></invoke>""",
        ]);
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(new Dictionary<string, string> { ["SKUA_FAKE_GAMEHOST_SCENARIO"] = scenario });
        Stopwatch waited = Stopwatch.StartNew();
        while ((await connection.StatusAsync(Ct)).Game.State != GameState.LoginScreen)
        {
            if (waited.Elapsed > Timeout)
                throw new TimeoutException("The headless Engine's game never reached the login screen.");
            await Task.Delay(50, Ct);
        }
        return (engine, connection);
    }

    private static string WriteLoopScript(EngineSandbox sandbox)
    {
        string file = Path.Combine(sandbox.SkuaDir, "Scripts", "Tests", "Loop.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            using System.Threading;
            using Skua.Core.Interfaces;

            public class TestScript
            {
                public void ScriptMain(IScriptInterface bot)
                {
                    while (!bot.ShouldExit)
                        Thread.Sleep(50);
                }
            }
            """);
        return file;
    }

    /// <summary>The take-over window for the sandbox's Engine Name, whose start records whether the name was free when it ran.</summary>
    private static Shown Show(EngineSandbox sandbox)
    {
        List<(bool LockHeld, bool SocketExists)> starts = [];
        TakeOverViewModel model = new(sandbox.Endpoint, () =>
        {
            starts.Add((EngineLock.IsHeld(sandbox.Endpoint.LockPath), File.Exists(sandbox.Endpoint.SocketPath)));
            return Task.CompletedTask;
        });
        TakeOverView view = new(model);
        Window window = new() { Width = 520, Height = 300, Content = view };
        window.Show();
        Shown shown = new(window, model, view, starts);
        model.QuitRequested += () => shown.QuitRequested = true;
        return shown;
    }

    private static void Click(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, $"{button.Content} is visible and enabled");
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    private static async Task PumpUntilAsync(Func<bool> done, string what)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > Timeout)
                throw new TimeoutException($"Waited {Timeout.TotalSeconds} s for {what}.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class Shown(Window window, TakeOverViewModel model, TakeOverView view, List<(bool LockHeld, bool SocketExists)> starts) : IAsyncDisposable
    {
        public Window Window { get; } = window;

        public TakeOverViewModel Model { get; } = model;

        public TakeOverView View { get; } = view;

        /// <summary>Each start of the app's Engine: whether another Engine still held the name then.</summary>
        public List<(bool LockHeld, bool SocketExists)> Starts { get; } = starts;

        public bool QuitRequested { get; set; }

        /// <summary>Looks, as the window does once it opens, and lays the view out for the result.</summary>
        public async Task LookAsync()
        {
            Task look = Model.LookAsync();
            await PumpUntilAsync(() => look.IsCompleted, "the look at the Engine");
            await look;
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public ValueTask DisposeAsync()
        {
            Window.Close();
            return ValueTask.CompletedTask;
        }
    }
}
