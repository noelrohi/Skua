using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;
using Skua.Core.ViewModels;
using static Skua.Avalonia.Tests.Ui;

namespace Skua.Avalonia.Tests;

/// <summary>The Options panels: game options that reach the game and persist, the lag killer in app mode, and themes across every window.</summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine and its game.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class OptionsTests(AppEngine app)
{
    [AvaloniaFact]
    public async Task A_game_option_changed_in_the_panel_reaches_the_game_at_once_and_is_saved_for_the_next_start()
    {
        IScriptOption options = app.Get<IScriptOption>();
        HostWindow window = await ShowAsync("Game");
        try
        {
            int before = KillLagCalls().Count;
            await ClickOptionAsync(window, "Lag Killer");
            Assert.True(options.LagKiller);
            await PumpUntilAsync(() => KillLagCalls().Skip(before).Contains("killLag true"), "the lag killer to reach the game");
            await ClickOptionAsync(window, "Hide Players");
            Assert.True(options.HidePlayers);
            // A Script's own change, on its thread, stays unsaved.
            await Task.Run(() => options.AggroMonsters = true, TestContext.Current.CancellationToken);

            List<string> saved = SavedOptions();
            Assert.Contains("LagKiller=True", saved);
            Assert.Contains("HidePlayers=True", saved);
            Assert.DoesNotContain("AggroMonsters=True", saved);

            // The panel follows a Script's change too.
            await Task.Run(() => options.HidePlayers = false, TestContext.Current.CancellationToken);
            await PumpUntilAsync(() => OptionCheckBox(window, "Hide Players")?.IsChecked == false, "the panel to show the Script's change");
        }
        finally
        {
            options.AggroMonsters = false;
            options.HidePlayers = false;
            // Unchecked in the panel, so it is saved off again.
            if (options.LagKiller)
                await ClickOptionAsync(window, "Lag Killer");
            window.Close();
        }
        Assert.Contains("LagKiller=False", SavedOptions());
    }

    [AvaloniaFact]
    public async Task After_a_login_in_the_app_the_lag_killer_follows_the_game_option()
    {
        IScriptOption options = app.Get<IScriptOption>();
        HostWindow window = await ShowAsync("Game");
        try
        {
            Assert.False(options.LagKiller);
            await LogInAsync();
            int loggedIn = KillLagCalls().Count;
            // The options tick every 20 ms; a headless Engine would have turned it on at once.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("killLag true", KillLagCalls().Skip(loggedIn));
            Assert.False(options.LagKiller);

            await ClickOptionAsync(window, "Lag Killer");
            await app.Engine.Rpc.LogoutAsync(TestContext.Current.CancellationToken);
            int loggedOut = KillLagCalls().Count;
            await LogInAsync();

            await PumpUntilAsync(() => KillLagCalls().Skip(loggedOut).Contains("killLag true"), "the lag killer after the login");
            Assert.True(options.LagKiller);
        }
        finally
        {
            if (options.LagKiller)
                await ClickOptionAsync(window, "Lag Killer");
            window.Close();
            await app.Engine.Rpc.LogoutAsync(TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task Switching_the_theme_restyles_every_open_window_and_is_kept()
    {
        AvaloniaThemeService themes = Assert.IsType<AvaloniaThemeService>(app.Get<IThemeService>());
        SkuaTheme skua = themes.Presets.Cast<SkuaTheme>().Single(t => t.Name == "Skua");
        SkuaTheme rbot = themes.Presets.Cast<SkuaTheme>().Single(t => t.Name == "RBot");
        HostWindow options = await ShowAsync("Game");
        HostWindow themesWindow = await ShowAsync("Application Themes");
        try
        {
            themes.SetCurrentTheme(skua);
            // Skua's theme adjusts its colours for contrast, so the accent is the adjusted one.
            await PumpUntilAsync(() => AccentOf(options) == themes.Accent && themes.CurrentTheme.Equals(skua), "the Skua theme");
            Assert.Equal(ThemeVariant.Dark, options.ActualThemeVariant);

            await PumpUntilAsync(() => Find<Button>(themesWindow, b => b.Content as string == "RBot") is not null, "the RBot theme's button");
            Button pick = Find<Button>(themesWindow, b => b.Content as string == "RBot")!;
            Ui.Click(pick);

            await PumpUntilAsync(() => options.ActualThemeVariant == ThemeVariant.Light && themesWindow.ActualThemeVariant == ThemeVariant.Light, "every window to turn light");
            Assert.Equal(rbot.Primary, AccentOf(options));
            Assert.Equal(rbot.Primary, AccentOf(themesWindow));
            // A control drawn with the accent picks it up: the editor's swatch shows it.
            Assert.Equal(rbot.Primary, ((ISolidColorBrush)Find<ColorSchemeEditorView>(themesWindow)!.FindControl<Border>("PrimarySwatch")!.Background!).Color);
            Assert.StartsWith("RBot,Light,#ff9c934e,", SavedSetting<string>("CurrentTheme"));
        }
        finally
        {
            themes.SetCurrentTheme(skua);
            options.Close();
            themesWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_theme_edited_and_saved_in_the_panel_is_listed_kept_and_removable()
    {
        AvaloniaThemeService themes = Assert.IsType<AvaloniaThemeService>(app.Get<IThemeService>());
        SkuaTheme skua = themes.Presets.Cast<SkuaTheme>().Single(t => t.Name == "Skua");
        HostWindow window = await ShowAsync("Application Themes");
        ColorSchemeEditorView editor = Find<ColorSchemeEditorView>(window)!;
        try
        {
            // The hue buttons set the active scheme's colour, the primary one.
            Ui.Click(Find<Button>(editor, b => ToolTip.GetTip(b) as string == "#E91E63")!);
            await PumpUntilAsync(() => themes.PrimaryColor == Color.Parse("#E91E63"), "the new primary colour");
            await PumpUntilAsync(() => AccentOf(window) == themes.Accent, "the window's accent");
            editor.FindControl<TextBox>("ThemeName")!.Text = "Pink";
            await PumpUntilAsync(() => true, "a layout pass");
            Ui.Click(editor.FindControl<Button>("SaveTheme")!);

            ItemsControl userThemes = Find<ApplicationThemesView>(window)!.FindControl<ItemsControl>("UserThemes")!;
            await PumpUntilAsync(() => Find<Button>(userThemes, b => b.Content as string == "Pink") is not null, "the saved theme's button");
            Assert.Contains(SavedSetting<StringCollection>("UserThemes")!.Cast<string>(), t => t.StartsWith("Pink,Dark,#ffe91e63,", StringComparison.Ordinal));
            Assert.StartsWith("Pink,", SavedSetting<string>("CurrentTheme"));

            Ui.Click(Find<Button>(userThemes, b => Equals(b.Tag, "remove"))!);
            await PumpUntilAsync(() => themes.UserThemes.Count == 0, "the theme to be removed");
            Assert.Equal(skua, themes.CurrentTheme);
            Assert.DoesNotContain(SavedSetting<StringCollection>("UserThemes")?.Cast<string>() ?? [], t => t.StartsWith("Pink,", StringComparison.Ordinal));
        }
        finally
        {
            themes.SetCurrentTheme(skua);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Open_Themes_Folder_and_Open_VSCode_go_through_the_apps_process_service()
    {
        HostWindow window = await ShowAsync("Application Themes");
        int before = Launches.Runs().Count;
        try
        {
            ApplicationThemesViewModel model = (ApplicationThemesViewModel)window.DataContext!;
            model.BackgroundTheme.OpenThemesFolderCommand.Execute(null);
            app.Get<ScriptLoaderViewModel>().OpenVSCodeCommand.Execute(null);

            await PumpUntilAsync(() => Launches.Runs().Count >= before + 2, "both launches");
            List<(string Program, string[] Arguments)> runs = [.. Launches.Runs().Skip(before)];
            Assert.Contains(runs, r => r.Program == "/usr/bin/open" && r.Arguments.SequenceEqual([ClientFileSources.SkuaThemesDIR]));
            Assert.Contains(runs, r => r.Program == "/usr/bin/open" && r.Arguments.SequenceEqual(["-b", "com.microsoft.VSCode", ClientFileSources.SkuaScriptsDIR]));
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<HostWindow> ShowAsync(string key)
    {
        _ = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        windows.ShowManagedWindow(key);
        await PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
        HostWindow window = windows.OpenWindow(key)!;
        await PumpUntilAsync(() => Find<UserControl>(window) is not null, $"the {key} view");
        // The themes and backgrounds show in expanders, closed at first.
        foreach (Expander expander in window.GetVisualDescendants().OfType<Expander>())
            expander.IsExpanded = true;
        await PumpUntilAsync(() => true, "a layout pass");
        return window;
    }

    private async Task LogInAsync()
    {
        await PumpUntilAsync(() => app.Flash.IsGameHostRunning, "the Game Host");
        LoginResult result = await app.Engine.Rpc.LoginAsync("Galanoth", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Galanoth", result.Server);
    }

    /// <summary>Clicks an option's check box as the developer does, after searching for it so it is on screen.</summary>
    private static async Task ClickOptionAsync(HostWindow window, string option)
    {
        Find<GameOptionsView>(window)!.FindControl<TextBox>("SearchBox")!.Text = option;
        // The search shows only that option, at the top of the list.
        await PumpUntilAsync(
            () => window.GetVisualDescendants().OfType<OptionItemView>().Count() == 1 && OptionCheckBox(window, option) is { Bounds.Width: > 0 },
            $"the {option} check box alone");
        await PumpUntilAsync(() => true, "a layout pass");
        CheckBox check = OptionCheckBox(window, option)!;
        Point center = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await PumpUntilAsync(() => true, "a layout pass");
    }

    private static CheckBox? OptionCheckBox(HostWindow window, string option) =>
        Find<OptionItemView>(window, v => v.Item.Content == option)?.Content as CheckBox;

    private static List<string> KillLagCalls() =>
        File.Exists(AppEngine.CallLog) ? File.ReadAllLines(AppEngine.CallLog).Where(l => l.StartsWith("killLag ", StringComparison.Ordinal)).ToList() : [];

    private static Color? AccentOf(Window window) =>
        window.TryFindResource("SystemAccentColor", window.ActualThemeVariant, out object? value) && value is Color color ? color : null;

    /// <summary>The game options a new start reads, from the settings file as a fresh settings service reads it.</summary>
    private static List<string> SavedOptions() => SavedSetting<StringCollection>(GameOptionEdits.SettingKey)?.Cast<string>().ToList() ?? [];

    private static T? SavedSetting<T>(string key)
    {
        UnifiedSettingsService fresh = new();
        fresh.Initialize(AppRole.Client);
        return fresh.Get<T>(key);
    }
}
