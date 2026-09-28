using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.ViewModels;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>The Scripts and Script Repo panels over the app's Engine, with the fake Game Host and the fake Script Source.</summary>
[Collection(nameof(GameViewTests))]
public sealed class ScriptsPanelTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task A_Script_picked_from_the_Script_Source_starts_logs_live_and_stops_from_the_panel()
    {
        AppEngine.GitHub.Commit("noelrohi", "Scripts", "Skua", new FakeScript(
            "Tests/PanelHello.cs", Script("""
                bot.Log("hello from the panel");
                while (!bot.ShouldExit)
                    Thread.Sleep(50);
                """), "Panel Hello", "Says hello, then waits to be stopped.", "test"));
        (Window scriptsWindow, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        ScriptLoaderViewModel scripts = (ScriptLoaderViewModel)loader.DataContext!;

        Ui.Click(loader.FindControl<Button>("SearchScripts")!);
        (Window repoWindow, ScriptRepoView repo) = await ShownAsync<ScriptRepoView>("Script Repo");
        Ui.Click(repo.FindControl<Button>("UpdateScripts")!);
        await Ui.PumpUntilAsync(() => repo.Shown.Any(s => s.FilePath == "Tests/PanelHello.cs" && s.Downloaded), "the updated Script in the list");
        Assert.StartsWith("Downloaded", repo.FindControl<TextBlock>("UpdateResult")!.Text);
        repo.FindControl<TextBox>("SearchBox")!.Text = "panel hello";
        await Ui.PumpUntilAsync(() => repo.Shown.Count == 1, "the search to keep one Script");
        Assert.Equal("Tests/PanelHello.cs", repo.Shown[0].FilePath);

        await Ui.PumpUntilAsync(() => StartButton(repo) is not null, "the Script's Start button");
        Ui.Click(StartButton(repo)!);
        Button toggle = loader.FindControl<Button>("ToggleScript")!;
        await Ui.PumpUntilAsync(() => scripts.ScriptStatus == "[Running]" && Ui.Text(toggle) == "Stop Script", "the panel to show the Script running");
        Assert.Equal("PanelHello.cs", loader.FindControl<TextBlock>("LoadedScript")!.Text);
        LogTabView log = Ui.Find<LogTabView>(loader)!;
        await Ui.PumpUntilAsync(() => Lines(log).Contains("hello from the panel"), "the Script's log line in the panel");

        await Ui.PumpUntilAsync(() => toggle.IsEffectivelyEnabled, "the Stop button");
        Ui.Click(toggle);
        await Ui.PumpUntilAsync(() => scripts.ScriptStatus == "[Stopped]" && Ui.Text(toggle) == "Start Script" && toggle.IsEffectivelyEnabled, "the panel to show the Script stopped");

        // The panel's stop is script_stop's, so the run says it was stopped.
        using EngineConnection connection = await ConnectAsync();
        ScriptStatusDto status = await connection.ScriptStatusAsync(Ct);
        Assert.Equal(ScriptState.Idle, status.State);
        Assert.Equal(("Tests/PanelHello.cs", ScriptOutcome.Stopped), (status.LastRun!.Script, status.LastRun.Outcome));
        repoWindow.Close();
        scriptsWindow.Close();
    }

    [AvaloniaFact]
    public async Task A_Script_started_with_the_CLI_shows_running_and_stopping_it_from_the_panel_ends_script_wait()
    {
        AppEngine.WriteScript("Tests/CliLoop.cs", Script("""
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        (Window window, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        Button toggle = loader.FindControl<Button>("ToggleScript")!;

        (int started, string startOut) = await CliAsync(["script", "start", "Tests/CliLoop.cs", "--no-update", "--json"]);
        Assert.True(started == 0, startOut);
        await Ui.PumpUntilAsync(() => Ui.Text(toggle) == "Stop Script" && toggle.IsEffectivelyEnabled, "the panel to show the CLI's Script running");

        Task<(int, string)> wait = CliAsync(["script", "wait", "--timeout", "60", "--json"]);
        // Give the wait time to connect and start waiting, as a user's would be.
        await Task.Delay(500, Ct);
        Assert.False(wait.IsCompleted, "skua script wait ended before the stop");
        Ui.Click(toggle);
        await Ui.PumpUntilAsync(() => wait.IsCompleted, "skua script wait to end");

        (int waited, string waitOut) = await wait;
        Assert.True(waited == 0, waitOut);
        using JsonDocument result = JsonDocument.Parse(waitOut);
        Assert.Equal("ended", result.RootElement.GetProperty("reason").GetString());
        JsonElement lastRun = result.RootElement.GetProperty("status").GetProperty("lastRun");
        Assert.Equal(("Tests/CliLoop.cs", "stopped"), (lastRun.GetProperty("script").GetString(), lastRun.GetProperty("outcome").GetString()));
        await Ui.PumpUntilAsync(() => Ui.Text(toggle) == "Start Script", "the panel to show the Script stopped");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Script_that_stops_itself_updates_the_panel_from_its_own_thread()
    {
        AppEngine.WriteScript("Tests/StopsItself.cs", Script("""
            Thread.Sleep(500);
            bot.Stop();
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        (Window window, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        ScriptLoaderViewModel scripts = (ScriptLoaderViewModel)loader.DataContext!;
        Button toggle = loader.FindControl<Button>("ToggleScript")!;
        using EngineConnection connection = await ConnectAsync();

        await connection.ScriptStartAsync("Tests/StopsItself.cs", cancellationToken: Ct);
        await Ui.PumpUntilAsync(() => Ui.Text(toggle) == "Stop Script", "the panel to show the Script running");
        // Core sends its stopped message on the Script's thread, and the panel's status changes on it.
        await Ui.PumpUntilAsync(() => scripts.ScriptStatus == "[Stopped]" && Ui.Text(toggle) == "Start Script" && toggle.IsEffectivelyEnabled,
            "the panel to show the Script stopped");

        ScriptStatusDto status = await connection.ScriptStatusAsync(Ct);
        // A Script's own stop isn't the window's, so its run still completes.
        Assert.Equal(("Tests/StopsItself.cs", ScriptOutcome.Completed), (status.LastRun!.Script, status.LastRun.Outcome));
        window.Close();
    }

    [AvaloniaFact]
    public async Task View_model_changes_from_a_background_thread_reach_the_panels_on_the_UI_thread()
    {
        (Window scriptsWindow, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        ScriptLoaderViewModel scripts = (ScriptLoaderViewModel)loader.DataContext!;
        string status = scripts.ScriptStatus;
        ScriptRepoViewModel repoModel = new(app.Get<Skua.Core.Interfaces.IGetScriptsService>(), app.Get<Skua.Core.Interfaces.IProcessService>());
        HostWindow repoWindow = new(repoModel);
        repoWindow.Show();
        ScriptRepoView repo = await FoundAsync<ScriptRepoView>(repoWindow);

        // As Core's stopped message does on a Script's thread, and as Core refills the Script Repo's list off the UI thread.
        await Task.Run(() =>
        {
            scripts.ToggleScriptEnabled = false;
            scripts.ScriptStatus = "[From a background thread]";
            repoModel.Scripts.AddRange([Info("Tests/One.cs", "One"), Info("Tests/Two.cs", "Two")]);
        }, Ct);

        Button toggle = loader.FindControl<Button>("ToggleScript")!;
        await Ui.PumpUntilAsync(() => loader.FindControl<TextBlock>("Status")!.Text == "Status: [From a background thread]" && !toggle.IsEffectivelyEnabled,
            "the Scripts panel to follow");
        await Ui.PumpUntilAsync(() => repo.Shown.Select(s => s.FilePath).SequenceEqual(["Tests/One.cs", "Tests/Two.cs"]), "the Script Repo to follow");

        scripts.ScriptStatus = status;
        scripts.ToggleScriptEnabled = true;
        repoWindow.Close();
        scriptsWindow.Close();
    }

    internal static ScriptInfoViewModel Info(string path, string name, string description = "", params string[] tags) =>
        new(new Skua.Core.Models.GitHub.ScriptInfo { FilePath = path, Name = name, FileName = Path.GetFileName(path), Description = description, Tags = tags });

    private static string Script(string body) => $$"""
        using System.Threading;
        using Skua.Core.Interfaces;

        public class TestScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                {{body}}
            }
        }
        """;

    private async Task<(Window, T)> OpenAsync<T>(string key) where T : Visual
    {
        // Core's main menu registers the managed windows as it is made.
        app.Get<MainMenuViewModel>();
        app.Get<AvaloniaWindowService>().ShowManagedWindow(key);
        return await ShownAsync<T>(key);
    }

    private async Task<(Window, T)> ShownAsync<T>(string key) where T : Visual
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
        Window window = windows.OpenWindow(key)!;
        return (window, await FoundAsync<T>(window));
    }

    private static async Task<T> FoundAsync<T>(Window window) where T : Visual
    {
        await Ui.PumpUntilAsync(() => Ui.Find<T>(window) is not null, $"a {typeof(T).Name}");
        return Ui.Find<T>(window)!;
    }

    private static Button? StartButton(ScriptRepoView repo) =>
        Ui.Find<Button>(repo, b => b.Content as string == "Start" && b.DataContext is ScriptInfoViewModel { FilePath: "Tests/PanelHello.cs" });

    private static IEnumerable<string> Lines(LogTabView log) => log.FindControl<ListBox>("Lines")!.Items.OfType<string>();

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");

    /// <summary>Runs the <c>skua</c> CLI against the app's Engine, and returns its exit code and output.</summary>
    internal static async Task<(int ExitCode, string Output)> CliAsync(string[] args)
    {
        ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "skua"), args)
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
        return (cli.ExitCode, cli.ExitCode == 0 ? await stdout : await stdout + await stderr);
    }
}
