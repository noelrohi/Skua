using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skua.Control;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Tests;

/// <summary>
/// A Script's stop and error, and a relogin, post notifications while the main window isn't in front, as the Windows tray's balloons show
/// while its window is hidden. Against the app's Engine and the fake Game Host, with a recording poster in place of Notification Center.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class ScriptStatusAlertsTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<(string Title, string Body)> _posted = [];

    [AvaloniaFact]
    public async Task With_the_main_window_closed_a_stop_an_error_and_a_relogin_each_post_a_notification()
    {
        AppEngine.WriteScript("Tests/AlertStops.cs", Script("""bot.Log("done");"""));
        AppEngine.WriteScript("Tests/AlertThrows.cs", Script("""throw new System.InvalidOperationException("Out of potions.");"""));
        IScriptOption options = app.Get<IScriptOption>();
        (bool autoRelogin, bool safeRelogin, int delay) = (options.AutoRelogin, options.SafeRelogin, options.ReloginTryDelay);
        Window window = new() { Title = "Skua" };
        CloseAndQuit rules = new(window, app.Engine.Rpc, app.Engine.Endpoint.Name, () => { });
        using ScriptStatusAlerts alerts = new(window, () => true, () => app.Get<IScriptPlayer>().Username, (title, body) => _posted.Add((title, body)));
        using EngineConnection connection = await ConnectAsync();
        try
        {
            // Closing the main window only hides it; the app, which stays active, keeps running.
            window.Show();
            window.Close();
            Assert.False(window.IsVisible);

            await RunAsync(connection, "Tests/AlertStops.cs");
            await Ui.PumpUntilAsync(() => _posted.Count == 1, "the stop's notification");
            Assert.Equal(("Script Stopped", ""), _posted[0]);

            await RunAsync(connection, "Tests/AlertThrows.cs");
            await Ui.PumpUntilAsync(() => _posted.Count == 2, "the error's notification");
            Assert.Equal(("Script Error", "Out of potions."), _posted[1]);

            options.AutoRelogin = true;
            options.SafeRelogin = false;
            options.ReloginTryDelay = 200;
            await connection.LoginAsync("Galanoth", cancellationToken: Ct);
            string username = app.Get<IScriptPlayer>().Username;
            Assert.False(string.IsNullOrEmpty(username));
            await AppEngine.DoAsync("lose-connection Your connection to the server has been lost.");
            await Ui.PumpUntilAsync(() => _posted.Count == 3, "the relogin's notification");
            // The error's stop wasn't posted again, before the relogin's.
            Assert.Equal([("Script Stopped", ""), ("Script Error", "Out of potions."), ("Relogin", $"Relogin triggered for {username}.")], _posted);
            await PlayingAsync(connection);
        }
        finally
        {
            (options.AutoRelogin, options.SafeRelogin, options.ReloginTryDelay) = (autoRelogin, safeRelogin, delay);
            await connection.LogoutAsync(Ct);
            // Quitting lets the window close for real.
            await rules.QuitAsync(ask: false);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task While_the_main_window_is_in_front_nothing_is_posted_but_minimised_or_behind_another_app_it_is()
    {
        AppEngine.WriteScript("Tests/AlertStops.cs", Script("""bot.Log("done");"""));
        AppEngine.WriteScript("Tests/AlertThrows.cs", Script("""throw new System.InvalidOperationException("Out of potions.");"""));
        bool active = true;
        Window window = new() { Title = "Skua" };
        using ScriptStatusAlerts alerts = new(window, () => active, () => app.Get<IScriptPlayer>().Username, (title, body) => _posted.Add((title, body)));
        using EngineConnection connection = await ConnectAsync();
        try
        {
            window.Show();

            await RunAsync(connection, "Tests/AlertStops.cs");
            await RunAsync(connection, "Tests/AlertThrows.cs");
            Assert.Empty(_posted);

            window.WindowState = WindowState.Minimized;
            await RunAsync(connection, "Tests/AlertStops.cs");
            await Ui.PumpUntilAsync(() => _posted.Count == 1, "the minimised window's notification");

            window.WindowState = WindowState.Normal;
            active = false;
            await RunAsync(connection, "Tests/AlertThrows.cs");
            await Ui.PumpUntilAsync(() => _posted.Count == 2, "the notification behind another app");

            Assert.Equal([("Script Stopped", ""), ("Script Error", "Out of potions.")], _posted);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Runs a Script to its end, then runs what its messages queued on the UI thread, before the test changes the window.</summary>
    private static async Task RunAsync(EngineConnection connection, string path)
    {
        await connection.ScriptStartAsync(path, cancellationToken: Ct);
        await connection.ScriptWaitAsync(60, Ct);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Waits for the relogin to log back in, so the logout after it isn't undone.</summary>
    private static async Task PlayingAsync(EngineConnection connection)
    {
        StatusDto status = await connection.StatusAsync(Ct);
        for (int i = 0; i < 1500 && status.Game.State != GameState.Playing; i++)
        {
            await Task.Delay(20, Ct);
            status = await connection.StatusAsync(Ct);
        }
        Assert.Equal(GameState.Playing, status.Game.State);
    }

    private static string Script(string body) => $$"""
        using Skua.Core.Interfaces;

        public class TestScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                {{body}}
            }
        }
        """;

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");
}
