using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skua.Control;
using Skua.Engine;
using Skua.Engine.Tests;
using StreamJsonRpc;

namespace Skua.Avalonia.Tests;

/// <summary>The main window's status strip and what the Skua Manager's launch did, over the app's Engine with the simulated game, the fake Keychain and servers API.</summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine and its game.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class StatusTests(AppEngine app)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task A_launch_says_who_it_logged_in_as_and_the_strip_shows_the_account_map_and_server_until_a_logout()
    {
        await using Shown shown = await ShowAsync();

        await LaunchAsync(shown, "Galanoth");

        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", shown.Launched.Text);
        await PumpUntilAsync(() => shown.Strip.Text.Contains("level"), "the strip to show the player");
        // The simulated game starts at level 10 and keeps its levels across logins.
        Assert.True(shown.Model.Level >= 10);
        Assert.Equal($"Engine default (app)  ·  logged in  ·  SkuaTester  ·  Galanoth  ·  battleon, Enter  ·  level {shown.Model.Level}  ·  no Script", shown.Strip.Text);

        await LogOutAsync(shown);
        Assert.Equal("Engine default (app)  ·  login screen  ·  no Script", shown.Strip.Text);
    }

    [AvaloniaFact]
    public async Task Log_in_goes_ahead_during_a_start_up_Scripts_update_while_a_Script_start_is_refused_until_it_ends()
    {
        await using Shown shown = await ShowAsync();
        string path = $"Tests/Updating{Guid.NewGuid():N}.cs";
        AppEngine.GitHub.Commit("noelrohi", "Scripts", "Skua", new FakeScript(path, "public class TestScript { }"));
        EngineScripts scripts = app.Get<EngineScripts>();
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AppEngine.GitHub.ClearRequests();
        AppEngine.GitHub.RawHeld = held.Task;
        Task<ScriptsUpdateResult> update;
        Exception? refused;
        try
        {
            // As the start-up check updates the Scripts, off the UI thread.
            update = Task.Run(scripts.UpdateAsync, TestContext.Current.CancellationToken);
            await PumpUntilAsync(() => AppEngine.GitHub.Requests.Any(r => r.StartsWith("/raw/", StringComparison.Ordinal)), "the update to start downloading");

            await LaunchAsync(shown, "Galanoth");
            Assert.False(update.IsCompleted, "the update was still in flight during the login");

            // As the Scripts panel's Start Script does.
            scripts.ScriptManager.SetLoadedScript(Path.Combine(AppEngine.SkuaDir, "Scripts", path));
            refused = await scripts.ScriptManager.StartScript();
        }
        finally
        {
            AppEngine.GitHub.RawHeld = Task.CompletedTask;
            held.TrySetResult();
        }
        await PumpUntilAsync(() => update.IsCompleted, "the update to finish");
        await update;

        Assert.False(shown.Model.MessageIsError, shown.Model.Message);
        Assert.Equal("Logged in as SkuaTester (the Test Account) on Galanoth.", shown.Launched.Text);
        Assert.Contains("update the Scripts", Assert.IsType<LocalRpcException>(refused).Message);
        Assert.False(scripts.ScriptManager.ScriptRunning);

        await LogOutAsync(shown);
    }

    [AvaloniaFact]
    public async Task The_strip_follows_moves_between_cells_and_level_ups_as_they_happen()
    {
        await using Shown shown = await ShowAsync();
        await LaunchAsync(shown, "Galanoth");
        await PumpUntilAsync(() => shown.Strip.Text.Contains("battleon, Enter  ·  level"), "the player on the strip");
        int level = shown.Model.Level!.Value;

        // Neither records an event, and nothing polls: the Engine says the status changed.
        await AppEngine.DoAsync("cell r2");
        await PumpUntilAsync(() => shown.Strip.Text.Contains("battleon, r2"), "the strip to show the new cell");
        // A level's worth of XP, whatever the XP toward the next level was.
        await AppEngine.DoAsync("gain 4000 0");
        await PumpUntilAsync(() => shown.Strip.Text.Contains($"level {level + 1}"), "the strip to show the new level");

        await LogOutAsync(shown);
    }

    [AvaloniaFact]
    public async Task A_login_from_skua_login_in_a_terminal_updates_the_strip()
    {
        await using Shown shown = await ShowAsync();

        ProcessResult login = await RunCliAsync("login", "Sir Ver");

        Assert.True(login.ExitCode == 0, $"skua login exited with {login.ExitCode}: {login.Stderr}");
        await PumpUntilAsync(() => shown.Strip.Text.Contains("level"), "the strip to show the CLI's login");
        Assert.Equal($"Engine default (app)  ·  logged in  ·  SkuaTester  ·  Sir Ver  ·  battleon, Enter  ·  level {shown.Model.Level}  ·  no Script", shown.Strip.Text);

        ProcessResult logout = await RunCliAsync("logout");

        Assert.True(logout.ExitCode == 0, $"skua logout exited with {logout.ExitCode}: {logout.Stderr}");
        await PumpUntilAsync(() => shown.Model.GameState == GameState.LoginScreen, "the strip to show the CLI's logout");
    }

    public enum Failure
    {
        NoActiveAccount,
        KeychainDenied,
        FullServer,
        OfflineServer,
    }

    [AvaloniaTheory]
    [InlineData(Failure.NoActiveAccount, "There is no account in Keychain under the service 'skua-test-account'")]
    [InlineData(Failure.KeychainDenied, "Couldn't read the account from Keychain (security exited with 128: security: SecKeychainItemCopyContent: User canceled the operation.)")]
    [InlineData(Failure.FullServer, "Artix is full (1500/1500); pick another server.")]
    [InlineData(Failure.OfflineServer, "Twig is offline; pick another server.")]
    public async Task A_failed_launch_says_why_and_the_next_one_goes_ahead(Failure failure, string message)
    {
        await using Shown shown = await ShowAsync();
        string tool = AppEngine.Keychain.Tool;
        string server = failure switch
        {
            Failure.FullServer => "Artix",
            Failure.OfflineServer => "Twig",
            _ => "Galanoth",
        };
        try
        {
            if (failure == Failure.NoActiveAccount)
                Directory.Delete(Path.Combine(AppEngine.Keychain.Items, FakeKeychain.DefaultService), recursive: true);
            if (failure == Failure.KeychainDenied)
                Environment.SetEnvironmentVariable(Keychain.ToolVariable, DenyingTool());

            await LaunchAsync(shown, server);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Keychain.ToolVariable, tool);
            if (failure == Failure.NoActiveAccount)
                AppEngine.Keychain.Add(FakeKeychain.DefaultService, AppEngine.Keychain.Username, AppEngine.Keychain.Password);
        }

        Assert.True(shown.Model.MessageIsError);
        Assert.Contains(message, shown.Launched.Text);
        Assert.Equal(GameState.LoginScreen, shown.Model.GameState);

        // With the cause gone, the next launch logs in.
        await LaunchAsync(shown, "Galanoth");
        await PumpUntilAsync(() => shown.Model.GameState == GameState.Playing, "the next login");
        Assert.False(shown.Model.MessageIsError);
        await LogOutAsync(shown);
    }

    [AvaloniaFact]
    public async Task The_password_never_reaches_the_logs_or_the_window()
    {
        await using Shown shown = await ShowAsync();
        await LaunchAsync(shown, "Galanoth");
        await PumpUntilAsync(() => shown.Model.GameState == GameState.Playing, "the login");
        string password = AppEngine.Keychain.Password;

        List<string> logged = [.. Directory.EnumerateFiles(app.Engine.Endpoint.LogFilesDir).Select(File.ReadAllText)];
        string? cursor = null;
        while (true)
        {
            LogPage page = await app.Engine.Rpc.LogsAsync(LogKind.All, cursor, 1000, cancellationToken: TestContext.Current.CancellationToken);
            logged.AddRange(page.Entries.Select(e => e.Text + e.Data));
            cursor = page.Next;
            if (page.Entries.Count == 0)
                break;
        }

        // The check can find it: the simulated game's scenario holds the password.
        Assert.Equal(1, Count(File.ReadAllText(Path.Combine(AppEngine.SkuaDir, "fake-gamehost.scenario")), password));
        Assert.Equal(0, logged.Sum(text => Count(text, password)));
        Assert.Equal(0, Count(shown.Strip.Text + shown.Launched.Text, password));

        await LogOutAsync(shown);
    }

    [AvaloniaFact]
    public async Task A_launch_from_the_Skua_Manager_logs_in_on_its_server_then_starts_its_Script()
    {
        await using Shown shown = await ShowAsync();
        string script = Path.Combine(AppEngine.SkuaDir, $"Launched{Guid.NewGuid():N}.cs");
        File.WriteAllText(script, """
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
        try
        {
            // What the app does once its window shows, given --server and --script.
            Task launch = shown.Model.LaunchAsync("Sir Ver", script);
            await PumpUntilAsync(() => launch.IsCompleted, "the launch");
            await launch;

            Assert.False(shown.Model.MessageIsError, shown.Model.Message);
            Assert.Equal($"Logged in as SkuaTester (the Test Account) on Sir Ver. Started {Path.GetFileName(script)}.", shown.Model.Message);
            StatusDto status = await app.Engine.Rpc.StatusAsync(TestContext.Current.CancellationToken);
            Assert.Equal(GameState.Playing, status.Game.State);
            Assert.Equal("Sir Ver", status.Game.Server);
            Assert.Equal(script, status.Script.Run?.Script);
        }
        finally
        {
            Task stop = app.Engine.Rpc.ScriptStopAsync(TestContext.Current.CancellationToken);
            await PumpUntilAsync(() => stop.IsCompleted, "the Script to stop");
            File.Delete(script);
        }
        await LogOutAsync(shown);
    }

    /// <summary>A window with what the launch did and the status strip, over the app's Engine at its login screen.</summary>
    private async Task<Shown> ShowAsync()
    {
        StatusViewModel model = new(app.Engine.Rpc, app.Engine.Endpoint.Name, "app");
        app.Engine.StatusChanged += model.Changed;
        LaunchMessage launched = new(model);
        StatusStrip strip = new(model);
        DockPanel.SetDock(launched, Dock.Top);
        Window window = new() { Width = 958, Height = 200, Content = new DockPanel { Children = { launched, strip } } };
        window.Show();
        Shown shown = new(window, model, launched, strip, () => app.Engine.StatusChanged -= model.Changed);
        // A test before may have left the game logged in, or restarted it.
        await PumpUntilAsync(() => model.GameState != GameState.NotStarted, "the Game Client to load");
        if (model.GameState != GameState.LoginScreen)
            await app.Engine.Rpc.LogoutAsync(TestContext.Current.CancellationToken);
        await PumpUntilAsync(() => model.GameState == GameState.LoginScreen, "the login screen");
        return shown;
    }

    /// <summary>Logs in on <paramref name="server"/> as the Skua Manager's launch does, and waits for it to finish.</summary>
    private static async Task LaunchAsync(Shown shown, string server)
    {
        Task launch = shown.Model.LaunchAsync(server, null);
        await PumpUntilAsync(() => launch.IsCompleted, "the launch");
        await launch;
    }

    /// <summary>Logs out as <c>skua logout</c> does, and waits for the strip to show the login screen.</summary>
    private async Task LogOutAsync(Shown shown)
    {
        Task logout = Task.Run(() => app.Engine.Rpc.LogoutAsync(TestContext.Current.CancellationToken));
        await PumpUntilAsync(() => logout.IsCompleted, "the logout");
        await logout;
        await PumpUntilAsync(() => shown.Model.GameState == GameState.LoginScreen, "the login screen");
    }

    /// <summary>A security tool that fails as macOS's does when the developer denies it access to the item.</summary>
    private static string DenyingTool()
    {
        string tool = Path.Combine(AppEngine.SkuaDir, "fake-security-denied");
        File.WriteAllText(tool, """
            #!/bin/sh
            echo "security: SecKeychainItemCopyContent: User canceled the operation." >&2
            exit 128
            """);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return tool;
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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

    private sealed record Shown(Window Window, StatusViewModel Model, LaunchMessage Launched, StatusStrip Strip, Action Unsubscribe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Unsubscribe();
            Window.Close();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
