using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;
using Skua.Core.ViewModels;
using static Skua.Avalonia.Tests.Ui;

namespace Skua.Avalonia.Tests;

/// <summary>The main window's title: Core's, with the build's version and, when the developer chooses, the username; and the Engine Name.</summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine and its game. Every window is closed, and the option turned off, in a finally.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class MainWindowTitleTests(AppEngine app)
{
    private const string ShowUsernameKey = "ShowUsernameInTitle";

    private static string Version => ClientFileSources.AssemblyVersion;

    [AvaloniaFact]
    public async Task The_title_is_Skua_and_the_builds_version_over_an_older_one_and_names_an_Engine_other_than_default()
    {
        ISettingsService settings = app.Get<ISettingsService>();
        // As a settings file an older build wrote.
        settings.Set("ApplicationVersion", "0.0.0.1");
        Window main = new();
        Window alice = new();
        try
        {
            MainWindowTitle.Follow(main, app.Engine.Services, EngineName.Default);
            MainWindowTitle.Follow(alice, app.Engine.Services, "alice");
            main.Show();
            alice.Show();
            await PumpUntilAsync(() => true, "a layout pass");

            Assert.Equal(typeof(MainWindowTitle).Assembly.GetName().Version!.ToString(), Version);
            Assert.NotEqual("0.0.0.0", Version);
            Assert.Equal($"Skua - {Version}", main.Title);
            Assert.Equal($"Skua - {Version} (Engine alice)", alice.Title);
            Assert.Equal(Version, SavedSetting<string>("ApplicationVersion"));
        }
        finally
        {
            main.Close();
            alice.Close();
        }
    }

    [AvaloniaFact]
    public async Task With_Show_Username_in_Title_on_the_title_ends_with_the_username_after_a_login_and_turning_it_off_removes_it_for_the_next_start_too()
    {
        MainViewModel model = app.Get<MainViewModel>();
        model.ShowUsernameInTitle = false;
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        Window main = new();
        List<bool> onUiThread = [];
        main.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TitleProperty)
                onUiThread.Add(Dispatcher.UIThread.CheckAccess());
        };
        try
        {
            MainWindowTitle.Follow(main, app.Engine.Services, EngineName.Default);
            main.Show();
            HostWindow options = await ShowOptionsAsync(windows);
            Assert.False(OptionCheckBox(options).IsChecked);
            Assert.Equal($"Skua - {Version}", main.Title);

            await ClickAsync(options, OptionCheckBox(options));
            Assert.True(model.ShowUsernameInTitle);
            Assert.True(SavedSetting<bool>(ShowUsernameKey));

            await PumpUntilAsync(() => app.Flash.IsGameHostRunning, "the Game Host");
            await app.Engine.Rpc.LoginAsync("Galanoth", cancellationToken: TestContext.Current.CancellationToken);
            // The view model looks for a new username every second, on a timer's thread.
            await PumpUntilAsync(() => main.Title == $"Skua - {Version} : {AppEngine.Keychain.Username}", "the username in the title");

            await ClickAsync(options, OptionCheckBox(options));
            Assert.False(model.ShowUsernameInTitle);
            await PumpUntilAsync(() => main.Title == $"Skua - {Version}", "the username to go");
            Assert.False(SavedSetting<bool>(ShowUsernameKey));
            Assert.NotEmpty(onUiThread);
            Assert.All(onUiThread, Assert.True);
        }
        finally
        {
            model.ShowUsernameInTitle = false;
            windows.OpenWindow("Application")?.Close();
            main.Close();
            await app.Engine.Rpc.LogoutAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<HostWindow> ShowOptionsAsync(AvaloniaWindowService windows)
    {
        windows.ShowManagedWindow("Application");
        await PumpUntilAsync(() => windows.OpenWindow("Application") is { } w && Find<ApplicationOptionsView>(w) is not null, "Application Options");
        return windows.OpenWindow("Application")!;
    }

    private static CheckBox OptionCheckBox(HostWindow window) =>
        (CheckBox)Find<OptionItemView>(window, v => v.Item.Content == "Show Username in Title")!.Content!;

    /// <summary>Clicks a check box as the developer does, once it is scrolled into view.</summary>
    private static async Task ClickAsync(HostWindow window, CheckBox check)
    {
        check.BringIntoView();
        await PumpUntilAsync(() => check.IsEffectivelyVisible && check.Bounds.Width > 0, "the check box on screen");
        await PumpUntilAsync(() => true, "a layout pass");
        Point center = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await PumpUntilAsync(() => true, "a layout pass");
    }

    /// <summary>A setting as the next start reads it, from the settings file through a fresh settings service.</summary>
    private static T? SavedSetting<T>(string key)
    {
        UnifiedSettingsService fresh = new();
        fresh.Initialize(AppRole.Client);
        return fresh.Get<T>(key);
    }
}
