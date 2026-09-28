using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.Engine;
using Skua.Engine.Tests;
using StreamJsonRpc;

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
        Button update = repo.FindControl<Button>("UpdateScripts")!;
        Ui.Click(update);
        // The Script is on disk, and may show as downloaded, before the update has finished.
        await Ui.PumpUntilAsync(() => update.IsEffectivelyEnabled, "the update to finish");
        Assert.StartsWith("Downloaded", repo.FindControl<TextBlock>("UpdateResult")!.Text);
        await Ui.PumpUntilAsync(() => repo.Shown.Any(s => s.FilePath == "Tests/PanelHello.cs" && s.Downloaded), "the updated Script in the list");
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
        ScriptRepoViewModel repoModel = new(app.Get<IGetScriptsService>(), app.Get<IProcessService>());
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

    [AvaloniaFact]
    public async Task Reset_asks_first_then_leaves_the_Scripts_folder_matching_the_Script_Source_with_a_local_edit_gone()
    {
        // Reset deletes files: only ever in the test's own data folder.
        Assert.StartsWith(AppEngine.SkuaDir + "/", ClientFileSources.SkuaScriptsDIR);
        string path = $"Tests/Reset{Guid.NewGuid():N}.cs";
        AppEngine.GitHub.Commit("noelrohi", "Scripts", "Skua", new FakeScript(path, "// from the Script Source", "Reset Me"));
        await app.Get<EngineScripts>().UpdateAsync();
        string edited = Path.Combine(ClientFileSources.SkuaScriptsDIR, path);
        File.WriteAllText(edited, "// edited on this Mac");
        string localOnly = AppEngine.WriteScript("Tests/ResetLocalOnly.cs", "// only on this Mac");
        string junk = ClientFileSources.SkuaJunkItemsFile;
        string? junkBefore = File.Exists(junk) ? File.ReadAllText(junk) : null;
        File.WriteAllText(junk, """["Reset Junk"]""");
        (Window scriptsWindow, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        Window? repoWindow = null;
        try
        {
            Ui.Click(loader.FindControl<Button>("SearchScripts")!);
            (repoWindow, ScriptRepoView repo) = await ShownAsync<ScriptRepoView>("Script Repo");
            Button reset = repo.FindControl<Button>("ResetScripts")!;
            TextBlock result = repo.FindControl<TextBlock>("UpdateResult")!;

            Ui.Click(reset);
            await Ui.PumpUntilAsync(() => repo.PendingReset is not null, "the question");
            Assert.Equal("Reset deletes everything in the Scripts folder, including Scripts you added or edited, then downloads every Script from noelrohi/Scripts@Skua again. Your junk items list is kept.",
                QuestionText(repo.PendingReset!));
            Ui.Click(repo.PendingReset!.CancelButton);
            await Ui.PumpUntilAsync(() => repo.PendingReset is null, "the question to go");
            Assert.True(File.Exists(localOnly));
            Assert.Equal("// edited on this Mac", File.ReadAllText(edited));

            result.Text = null;
            Ui.Click(reset);
            await Ui.PumpUntilAsync(() => repo.PendingReset is not null, "the question again");
            Ui.Click(repo.PendingReset!.ConfirmButton);
            await Ui.PumpUntilAsync(() => reset.IsEffectivelyEnabled && result.Text is { Length: > 0 } text && text != "Resetting the Scripts…", "the reset to finish");

            Assert.Matches(@"^Downloaded \d+ Scripts \(full download\)\.$", result.Text);
            List<string> inSource = [.. (await app.Get<IGetScriptsService>().FetchScriptsAsync(Ct)).Select(s => s.FilePath).Order(StringComparer.Ordinal)];
            Assert.Equal(inSource, ScriptsOnDisk());
            Assert.Equal("// from the Script Source", File.ReadAllText(edited));
            Assert.False(File.Exists(localOnly));
            Assert.Equal("""["Reset Junk"]""", File.ReadAllText(junk));
        }
        finally
        {
            if (junkBefore is null)
                File.Delete(junk);
            else
                File.WriteAllText(junk, junkBefore);
            repoWindow?.Close();
            scriptsWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task Reset_is_refused_with_a_message_while_a_Script_runs_and_deletes_nothing()
    {
        AppEngine.WriteScript("Tests/ResetLoop.cs", Script("""
            while (!bot.ShouldExit)
                Thread.Sleep(50);
            """));
        string localOnly = AppEngine.WriteScript("Tests/ResetKeptWhileRunning.cs", "// only on this Mac");
        (Window scriptsWindow, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        using EngineConnection connection = await ConnectAsync();
        Window? repoWindow = null;
        try
        {
            Ui.Click(loader.FindControl<Button>("SearchScripts")!);
            (repoWindow, ScriptRepoView repo) = await ShownAsync<ScriptRepoView>("Script Repo");
            await connection.ScriptStartAsync("Tests/ResetLoop.cs", cancellationToken: Ct);
            TextBlock result = repo.FindControl<TextBlock>("UpdateResult")!;
            result.Text = null;

            Ui.Click(repo.FindControl<Button>("ResetScripts")!);
            await Ui.PumpUntilAsync(() => repo.PendingReset is not null, "the question");
            Ui.Click(repo.PendingReset!.ConfirmButton);
            await Ui.PumpUntilAsync(() => result.Text is { Length: > 0 } text && text != "Resetting the Scripts…", "the refusal");

            Assert.Equal("Can't reset the Scripts while Tests/ResetLoop.cs is running; stop it first with 'skua script stop'.", result.Text);
            Assert.Equal("// only on this Mac", File.ReadAllText(localOnly));
            Assert.Equal(ScriptState.Running, (await connection.ScriptStatusAsync(Ct)).State);
        }
        finally
        {
            await connection.ScriptStopAsync(Ct);
            repoWindow?.Close();
            scriptsWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task Reset_is_refused_as_busy_while_a_Scripts_update_is_in_flight()
    {
        string localOnly = AppEngine.WriteScript("Tests/ResetKeptWhileUpdating.cs", "// only on this Mac");
        AppEngine.GitHub.Commit("noelrohi", "Scripts", "Skua", new FakeScript($"Tests/Held{Guid.NewGuid():N}.cs", "// held"));
        EngineScripts scripts = app.Get<EngineScripts>();
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AppEngine.GitHub.ClearRequests();
        AppEngine.GitHub.RawHeld = held.Task;
        Task<ScriptsUpdateResult> update = Task.Run(scripts.UpdateAsync, Ct);
        try
        {
            await AppEngine.GitHub.WaitForRawRequestAsync();
            LocalRpcException refused = await Assert.ThrowsAsync<LocalRpcException>(scripts.ResetAsync);

            Assert.Equal(ErrorCodes.ToWire(ErrorCode.Busy), refused.ErrorCode);
            Assert.False(update.IsCompleted);
            Assert.True(File.Exists(localOnly));
        }
        finally
        {
            AppEngine.GitHub.RawHeld = Task.CompletedTask;
            held.SetResult();
            await update;
        }
    }

    [AvaloniaFact]
    public async Task Open_in_VSCode_on_a_Scripts_context_menu_goes_through_the_apps_process_service()
    {
        string path = $"Tests/OpenMe{Guid.NewGuid():N}.cs";
        AppEngine.GitHub.Commit("noelrohi", "Scripts", "Skua", new FakeScript(path, "// open me", "Open Me"));
        await app.Get<EngineScripts>().UpdateAsync();
        (Window scriptsWindow, ScriptLoaderView loader) = await OpenAsync<ScriptLoaderView>("Scripts");
        Window? repoWindow = null;
        ContextMenu? menu = null;
        try
        {
            Ui.Click(loader.FindControl<Button>("SearchScripts")!);
            (repoWindow, ScriptRepoView repo) = await ShownAsync<ScriptRepoView>("Script Repo");
            ((ScriptRepoViewModel)repo.DataContext!).RefreshScriptsCommand.Execute(null);
            repo.FindControl<TextBox>("SearchBox")!.Text = path;
            await Ui.PumpUntilAsync(() => repo.Shown is [{ Downloaded: true } script] && script.FilePath == path, "the downloaded Script alone in the list");
            await Ui.PumpUntilAsync(() => ScriptCard(repo, path) is not null, "the Script's card");
            Border card = ScriptCard(repo, path)!;
            menu = card.ContextMenu!;
            menu.Open(card);
            await Ui.PumpUntilAsync(() => menu.IsOpen, "the context menu");
            MenuItem open = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Open in VSCode");
            Assert.Equal(["Download", "Delete", "Open in VSCode", "Load", "Start"], menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
            Assert.True(open.IsEffectivelyEnabled);
            int before = Launches.Runs().Count;

            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            await Ui.PumpUntilAsync(() => Launches.Runs().Count > before, "the launch");
            // The tests' app has no code command, so VS Code opens by its bundle id; the runner records it and opens nothing.
            (string program, string[] arguments) = Assert.Single(Launches.Runs().Skip(before));
            Assert.Equal("/usr/bin/open", program);
            Assert.Equal(["-b", "com.microsoft.VSCode", ClientFileSources.SkuaScriptsDIR, Path.Combine(ClientFileSources.SkuaScriptsDIR, path)], arguments);
        }
        finally
        {
            menu?.Close();
            repoWindow?.Close();
            scriptsWindow.Close();
        }
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
        // A Script that a failed test left running would fail this test's start as well.
        using (EngineConnection connection = await ConnectAsync())
            await connection.ScriptStopAsync(Ct);
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

    private static Border? ScriptCard(ScriptRepoView repo, string path) =>
        Ui.Find<Border>(repo, b => b.ContextMenu is not null && b.DataContext is ScriptInfoViewModel script && script.FilePath == path);

    private static string? QuestionText(ConfirmDialog question) => ((SelectableTextBlock)((StackPanel)question.Content!).Children[0]).Text;

    /// <summary>The Scripts folder's files, relative to it, but for the junk items list and the compile cache.</summary>
    private static List<string> ScriptsOnDisk()
    {
        string folder = ClientFileSources.SkuaScriptsDIR;
        return [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => f != ClientFileSources.SkuaJunkItemsFile)
            .Select(f => Path.GetRelativePath(folder, f))
            .Where(f => !f.StartsWith("Cached-Scripts/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

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
