using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.Engine;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The app never takes focus while another app is frontmost (#142): a window a Script run makes the app show waits, with a notification,
/// until the developer brings Skua forward. Against the app's Engine and the fake Game Host, with a recording poster.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class ForegroundTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<(string Title, string Body)> _posted = [];

    [AvaloniaFact]
    public async Task Behind_another_app_a_Scripts_options_window_waits_with_a_notification_until_Skua_comes_forward()
    {
        // Core opens a Script's options window as it starts, and CoreBots again as it restarts after a relogin.
        string id = Guid.NewGuid().ToString("N")[..8];
        string path = $"Tests/Foreground{id}.cs";
        AppEngine.WriteScript(path, $$"""
            using System.Collections.Generic;
            using Skua.Core.Interfaces;
            using Skua.Core.Options;

            public class TestScript
            {
                public string OptionsStorage = "Foreground{{id}}";

                public bool DontPreconfigure = false;

                public List<IOption> Options = new() { new Option<int>("count", "Count", "How many to farm.", 5) };

                public void ScriptMain(IScriptInterface bot) => bot.Log("started");
            }
            """);
        bool front = false;
        Foreground foreground = app.Get<Foreground>();
        AvaloniaDialogService dialogs = app.Get<AvaloniaDialogService>();
        (Func<bool> frontmost, Action<string, string> notify, Action<Window>? created) = (foreground.Frontmost, foreground.Notify, dialogs.WindowCreated);
        List<DialogWindow> opened = [];
        foreground.Frontmost = () => front;
        foreground.Notify = (title, body) => _posted.Add((title, body));
        dialogs.WindowCreated = w =>
        {
            created?.Invoke(w);
            if (w is DialogWindow dialog)
                opened.Add(dialog);
        };
        Window back = new() { Title = "Skua" };
        using EngineConnection connection = await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");
        try
        {
            await connection.ScriptStartAsync(path, cancellationToken: Ct);
            await Ui.PumpUntilAsync(() => opened.Count == 1, "the options window to be made");
            Dispatcher.UIThread.RunJobs();
            Assert.False(opened[0].IsVisible, "The options window showed while another app was frontmost.");
            ScriptWaitResult ended = await connection.ScriptWaitAsync(60, Ct);
            Assert.Equal(ScriptWaitReason.Ended, ended.Reason);
            Assert.False(opened[0].IsVisible, "The options window showed while another app was frontmost.");
            Assert.Equal([("Skua: Options", "Waiting for you in Skua.")], _posted);

            // The developer switches to Skua: one of its windows becomes active.
            front = true;
            back.Show();
            back.Activate();
            await Ui.PumpUntilAsync(() => opened[0] is { IsVisible: true, DataContext: OptionContainerViewModel }, "the options window once Skua is in front");
            Assert.Single(_posted);
        }
        finally
        {
            await connection.ScriptStopAsync(Ct);
            front = true;
            foreground.BroughtForward();
            Dispatcher.UIThread.RunJobs();
            foreach (DialogWindow dialog in opened)
                dialog.Close();
            back.Close();
            (foreground.Frontmost, foreground.Notify, dialogs.WindowCreated) = (frontmost, notify, created);
        }
    }

    [AvaloniaFact]
    public async Task Behind_another_app_a_Script_restarted_after_a_relogin_goes_on_with_its_saved_options_and_shows_no_window()
    {
        // CoreBots restarts a Script through Core's RestartScriptAsync once a relogin has stopped it, and opens its options window again (#144).
        string id = Guid.NewGuid().ToString("N")[..8];
        string path = $"Tests/Restart{id}.cs";
        string restarted = Path.Combine(ClientFileSources.SkuaDIR, $"Restart{id}.started");
        AppEngine.WriteScript(path, $$"""
            using System.Collections.Generic;
            using System.IO;
            using System.Threading.Tasks;
            using Skua.Core.Interfaces;
            using Skua.Core.Options;

            public class TestScript
            {
                public string OptionsStorage = "Restart{{id}}";

                public bool DontPreconfigure = true;

                public List<IOption> Options = new() { new Option<int>("count", "Count", "How many to farm.", 5) };

                public void ScriptMain(IScriptInterface bot)
                {
                    if (!File.Exists(@"{{restarted}}"))
                    {
                        File.WriteAllText(@"{{restarted}}", "");
                        Task.Run(() => bot.Manager.RestartScriptAsync());
                        return;
                    }
                    bot.Config.Configure();
                    bot.Log($"restarted count={bot.Config.Get<int>("count")}");
                }
            }
            """);
        Foreground foreground = app.Get<Foreground>();
        AvaloniaDialogService dialogs = app.Get<AvaloniaDialogService>();
        (Func<bool> frontmost, Action<string, string> notify, Action<Window>? created) = (foreground.Frontmost, foreground.Notify, dialogs.WindowCreated);
        List<Window> opened = [];
        foreground.Frontmost = () => false;
        foreground.Notify = (title, body) => _posted.Add((title, body));
        dialogs.WindowCreated = w =>
        {
            created?.Invoke(w);
            opened.Add(w);
        };
        using EngineConnection connection = await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");
        try
        {
            // An agent's start that stores the option, so the Script has saved options.
            await connection.ScriptStartAsync(path, new Dictionary<string, string> { ["count"] = "7" }, cancellationToken: Ct);

            await connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == "restarted count=7");
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(opened);
            Assert.Empty(_posted);
        }
        finally
        {
            await connection.ScriptStopAsync(Ct);
            foreground.Frontmost = () => true;
            foreground.BroughtForward();
            Dispatcher.UIThread.RunJobs();
            foreach (Window window in opened)
                window.Close();
            (foreground.Frontmost, foreground.Notify, dialogs.WindowCreated) = (frontmost, notify, created);
            File.Delete(restarted);
        }
    }

    [AvaloniaFact]
    public async Task Behind_another_app_a_managed_window_waits_until_Skua_comes_forward_and_in_front_it_shows_at_once_without_a_notification()
    {
        bool front = false;
        Foreground foreground = app.Get<Foreground>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        // Core's main menu registers the managed windows as it is made.
        _ = app.Get<MainMenuViewModel>();
        Assert.True(windows.CanShow("Console"));
        (Func<bool> frontmost, Action<string, string> notify) = (foreground.Frontmost, foreground.Notify);
        foreground.Frontmost = () => front;
        foreground.Notify = (title, body) => _posted.Add((title, body));
        windows.OpenWindow("Console")?.Close();
        try
        {
            windows.ShowManagedWindow("Console");
            windows.ShowManagedWindow("Console");
            Assert.Null(windows.OpenWindow("Console"));
            Assert.Equal([("Skua: Console", "Waiting for you in Skua.")], _posted);

            front = true;
            foreground.BroughtForward();
            await Ui.PumpUntilAsync(() => windows.OpenWindow("Console") is { IsVisible: true }, "the Console once Skua is in front");
            windows.OpenWindow("Console")!.Close();
            await Ui.PumpUntilAsync(() => windows.OpenWindow("Console") is null, "the Console to close");

            windows.ShowManagedWindow("Console");
            Assert.True(windows.OpenWindow("Console") is { IsVisible: true }, "The Console didn't show at once with Skua in front.");
            Assert.Single(_posted);
        }
        finally
        {
            front = true;
            foreground.BroughtForward();
            Dispatcher.UIThread.RunJobs();
            windows.OpenWindow("Console")?.Close();
            (foreground.Frontmost, foreground.Notify) = (frontmost, notify);
        }
    }
}
