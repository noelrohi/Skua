using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Control;

namespace Skua.Avalonia.Tests;

/// <summary>
/// Closing the main window keeps the app's Engine playing, headless; quitting asks first while a Script runs. The host's shutdown is only
/// counted here: the app's Engine serves the whole test run.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class CloseAndQuitTests(AppEngine app)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Closing_the_window_hides_it_the_Game_View_goes_headless_and_skua_status_keeps_working_until_it_reopens()
    {
        await using Shown shown = await ShowAsync();
        int from = AppEngine.Calls().Length;

        shown.Window.Close();
        await PumpUntilAsync(() => AppEngine.Calls().Skip(from).Contains("view headless"), "the Game View to go headless");

        Assert.False(shown.Window.IsVisible);
        Assert.False(shown.View.IsLive);
        Assert.Equal(0, shown.Shutdowns);
        ProcessResult status = await RunCliAsync("status", "--json");
        Assert.True(status.ExitCode == 0, $"skua status exited with {status.ExitCode}: {status.Stderr}");
        Assert.Contains("\"gameHostUp\": true", status.Stdout);

        from = AppEngine.Calls().Length;
        shown.Rules.Reopen();
        await PumpUntilAsync(() => AppEngine.Calls().Skip(from).Contains("view live"), "the Game View to go live again");

        Assert.True(shown.Window.IsVisible);
        Assert.True(shown.View.IsLive);
    }

    [AvaloniaFact]
    public async Task Quitting_while_a_Script_runs_asks_first_and_Cancel_keeps_playing()
    {
        await using Shown shown = await ShowAsync();
        await using Running running = await StartScriptAsync();

        Task<bool> quitting = shown.Rules.QuitAsync(ask: true);
        await PumpUntilAsync(() => shown.Rules.Pending is not null, "the question");
        ConfirmDialog question = shown.Rules.Pending!;

        Assert.Equal("The Script Tests/Loop.cs is running. Quitting stops it, closes the game and stops Engine 'default', for skua and MCP too.", ((SelectableTextBlock)((StackPanel)question.Content!).Children[0]).Text);
        Click(question, question.CancelButton);
        await PumpUntilAsync(() => quitting.IsCompleted, "the quit to be called off");

        Assert.False(await quitting);
        Assert.False(shown.Rules.IsQuitting);
        Assert.Equal(0, shown.Shutdowns);
        Assert.Equal(ScriptState.Running, (await app.Engine.Rpc.ScriptStatusAsync(Ct)).State);

        quitting = shown.Rules.QuitAsync(ask: true);
        await PumpUntilAsync(() => shown.Rules.Pending is not null, "the question again");
        Click(shown.Rules.Pending!, shown.Rules.Pending!.ConfirmButton);
        await PumpUntilAsync(() => quitting.IsCompleted, "the quit");

        Assert.True(await quitting);
        Assert.True(shown.Rules.IsQuitting);
        Assert.Equal(1, shown.Shutdowns);
    }

    [AvaloniaFact]
    public async Task Quitting_with_no_Script_running_quits_without_asking()
    {
        await using Shown shown = await ShowAsync();

        Task<bool> quitting = shown.Rules.QuitAsync(ask: true);
        await PumpUntilAsync(() => quitting.IsCompleted, "the quit");

        Assert.True(await quitting);
        Assert.Null(shown.Rules.Pending);
        Assert.Equal(1, shown.Shutdowns);
    }

    [AvaloniaFact]
    public async Task A_quit_without_asking_as_on_SIGTERM_never_asks_and_ends_a_question_on_screen()
    {
        await using Shown shown = await ShowAsync();
        await using Running running = await StartScriptAsync();
        Task<bool> asking = shown.Rules.QuitAsync(ask: true);
        await PumpUntilAsync(() => shown.Rules.Pending is not null, "the question");
        ConfirmDialog question = shown.Rules.Pending!;

        Assert.True(await shown.Rules.QuitAsync(ask: false));
        await PumpUntilAsync(() => asking.IsCompleted, "the question to go");

        Assert.True(await asking);
        Assert.False(question.IsVisible);
        Assert.Equal(1, shown.Shutdowns);
        // Once quitting, asking again quits too.
        Assert.True(await shown.Rules.QuitAsync(ask: true));
        Assert.Equal(1, shown.Shutdowns);
    }

    /// <summary>A main window with the Game View, live, under the close and quit rules.</summary>
    private async Task<Shown> ShowAsync()
    {
        GameView view = new(app.Flash);
        Window window = new() { Width = 958, Height = 550, Content = view };
        Shown shown = new(window, view);
        shown.Rules = new CloseAndQuit(window, app.Engine.Rpc, app.Engine.Endpoint.Name, () => shown.Shutdowns++);
        window.Show();
        await PumpUntilAsync(() => view.IsLive && view.Bounds.Width > 0, "the view to be live");
        return shown;
    }

    /// <summary>Starts a Script that runs until stopped in the app's Engine; disposing stops it.</summary>
    private async Task<Running> StartScriptAsync()
    {
        string file = Path.Combine(AppEngine.SkuaDir, "Scripts", "Tests", "Loop.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """
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
            """, Ct);
        Task<ScriptStartResult> start = Task.Run(() => app.Engine.Rpc.ScriptStartAsync(file, null, null, null, Ct));
        await PumpUntilAsync(() => start.IsCompleted, "the Script to start");
        await start;
        return new Running(app);
    }

    private static void Click(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"{button.Content} is enabled");
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "skua"), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[EngineEndpoint.SkuaDirVariable] = AppEngine.SkuaDir;
        // No auto-start: the app's Engine is the only one.
        startInfo.Environment[EngineClientOptions.EngineExecutableVariable] = "/usr/bin/false";
        using Process cli = Process.Start(startInfo)!;
        Task<string> stdout = cli.StandardOutput.ReadToEndAsync();
        Task<string> stderr = cli.StandardError.ReadToEndAsync();
        // The Engine answers in this process, whose UI thread the test holds: keep it running while the CLI waits.
        await PumpUntilAsync(() => cli.HasExited, $"skua {string.Join(' ', arguments)}");
        return new ProcessResult(cli.ExitCode, await stdout, await stderr);
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

    private sealed class Shown(Window window, GameView view) : IAsyncDisposable
    {
        public Window Window { get; } = window;

        public GameView View { get; } = view;

        public CloseAndQuit Rules { get; set; } = null!;

        /// <summary>How often the rules ended the app's lifetime.</summary>
        public int Shutdowns { get; set; }

        public async ValueTask DisposeAsync()
        {
            // Quitting lets the window close for real.
            await Rules.QuitAsync(ask: false);
            Window.Close();
        }
    }

    private sealed class Running(AppEngine app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Task<ScriptStopResult> stop = Task.Run(() => app.Engine.Rpc.ScriptStopAsync(CancellationToken.None));
            await PumpUntilAsync(() => stop.IsCompleted, "the Script to stop");
            await stop;
        }
    }
}
