using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.Avalonia.Tests;

/// <summary>The main menu, the Logs panel, and every view this app has: each binds to its view model without a binding error.</summary>
[Collection(nameof(GameViewTests))]
public sealed class PanelTests(AppEngine app)
{
    [AvaloniaFact]
    public void Every_view_model_with_a_view_resolves_from_the_app_container()
    {
        foreach (Type type in ViewLocator.ViewModelTypes)
            Assert.True(ViewLocator.HasView(Resolve(type)), $"{type.Name} has no view");
        Assert.Equal(
            [
                nameof(AboutViewModel), nameof(AdvancedSkillsViewModel), nameof(ApplicationOptionsViewModel), nameof(ApplicationThemesViewModel), nameof(AutoViewModel),
                nameof(BotWindowViewModel),
                nameof(CBOLoadoutViewModel), nameof(CBOOtherOptionsViewModel), nameof(CBOptionsViewModel), nameof(ChangeLogsViewModel), nameof(ConsoleViewModel),
                nameof(CoreBotsViewModel), nameof(CurrentDropsViewModel), nameof(FastTravelViewModel), nameof(GameOptionsViewModel), nameof(GitHubAuthViewModel),
                nameof(GoalsViewModel), nameof(GrabberViewModel), nameof(HotKeyItemViewModel),
                nameof(HotKeysViewModel), nameof(JumpViewModel), nameof(JunkItemsViewModel), nameof(LoaderViewModel), nameof(LogTabViewModel), nameof(LogsViewModel),
                nameof(ManagerAccountsViewModel), nameof(ManagerMainViewModel), nameof(NotifyDropViewModel), nameof(PacketInterceptorViewModel),
                nameof(PacketLoggerViewModel), nameof(PacketSpammerViewModel), nameof(PluginsViewModel), nameof(RunningViewModel),
                nameof(RuntimeHelpersViewModel), nameof(ScriptLoaderViewModel), nameof(ScriptRepoViewModel), nameof(ScriptStatsViewModel), nameof(UpdatesViewModel),
            ],
            ViewLocator.ViewModelTypes.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task Each_view_binds_to_its_view_model_with_no_binding_errors()
    {
        // The Script Repo's list and its item template bind too.
        ScriptRepoViewModel repo = new(app.Get<IGetScriptsService>(), app.Get<IProcessService>());
        repo.Scripts.Add(ScriptsPanelTests.Info("Farm/Leveling.cs", "Leveling", "Levels up.", "farm", "xp"));
        List<object> viewModels = [.. ViewLocator.ViewModelTypes.Where(t => t != typeof(ScriptRepoViewModel)).Select(Resolve), repo];
        using BindingErrors errors = new();

        foreach (object viewModel in viewModels)
        {
            HostWindow window = new(viewModel);
            window.Show();
            await Ui.PumpUntilAsync(() => Ui.Find<UserControl>(window) is not null, $"{viewModel.GetType().Name}'s view");
            await Ui.PumpUntilAsync(() => true, "a layout pass");
            window.Close();
        }

        Assert.Empty(errors.Lines);
        Assert.NotNull(errors);
    }

    [AvaloniaFact]
    public void The_main_menu_enables_the_panels_with_views_and_the_items_with_their_own_command_and_shows_the_rest_disabled()
    {
        MainMenuViewModel viewModel = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();

        Menu menu = MainMenus.InWindow(viewModel, windows);
        NativeMenu native = MainMenus.Native(viewModel, windows);

        List<MenuItem> leaves = Leaves(menu.Items.OfType<MenuItem>()).ToList();
        Assert.Equal(
            [
                "Application", "Application Themes", "Bank", "Console", "CoreBots", "Current Drops", "Fast Travel", "Game", "Grabber", "HotKeys", "Interceptor",
                "Junk Items", "Loader", "Logger", "Logs", "Runtime", "Scripts", "Skills", "Spammer", "Stats", "View Plugins",
            ],
            leaves.Where(i => i.IsEnabled).Select(i => (string)i.Header!).Order(StringComparer.Ordinal));
        List<NativeMenuItem> nativeLeaves = native.Items.OfType<NativeMenuItem>().SelectMany(i => i.Menu!.Items.OfType<NativeMenuItem>()).ToList();
        Assert.Equal(
            [
                "Application", "Application Themes", "Console", "CoreBots", "Current Drops", "Fast Travel", "Game", "Grabber", "HotKeys", "Interceptor",
                "Junk Items", "Loader", "Logger", "Runtime", "Show Bank", "Show Logs", "Show Scripts", "Show Skills", "Spammer", "Stats", "View Plugins",
            ],
            nativeLeaves.Where(i => i.IsEnabled).Select(i => i.Header!).Order(StringComparer.Ordinal));
        Assert.Equal(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!), native.Items.OfType<NativeMenuItem>().Select(i => i.Header));
    }

    [AvaloniaFact]
    public async Task The_Scripts_menu_item_opens_the_Scripts_window_once()
    {
        MainMenuViewModel viewModel = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        MenuItem scripts = MainMenus.InWindow(viewModel, windows).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Scripts");

        scripts.Command!.Execute(null);
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Scripts") is not null, "the Scripts window");
        HostWindow window = windows.OpenWindow("Scripts")!;
        scripts.Command.Execute(null);

        Assert.Same(window, windows.OpenWindow("Scripts"));
        Assert.Equal("Load Script", window.Title);
        Assert.IsType<ScriptLoaderViewModel>(window.DataContext);
        window.Close();
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Scripts") is null, "the closed window to be forgotten");
    }

    [AvaloniaFact]
    public async Task The_Logs_panel_shows_Script_debug_and_flash_lines_live_and_redacted()
    {
        MainMenuViewModel _ = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        windows.ShowManagedWindow("Logs");
        await Ui.PumpUntilAsync(() => windows.OpenWindow("Logs") is not null, "the Logs window");
        HostWindow window = windows.OpenWindow("Logs")!;
        LogsViewModel logs = (LogsViewModel)window.DataContext!;
        ILogService log = app.Get<ILogService>();
        string id = Guid.NewGuid().ToString("N")[..8];

        // From other threads, as a Script and the Game Host write them.
        await Task.Run(() =>
        {
            log.ScriptLog($"script line {id}");
            log.DebugLog($"debug line {id} password={AppEngine.Secret}");
            log.FlashLog($"flash line {id}");
        }, TestContext.Current.CancellationToken);

        foreach ((string tab, string line) in new[] { ("Script", $"script line {id}"), ("Debug", $"debug line {id} password=[redacted]"), ("Flash", $"flash line {id}") })
        {
            logs.SelectedTab = logs.LogTabs.Single(t => t.Title == tab);
            await Ui.PumpUntilAsync(
                () => Ui.Find<LogTabView>(window) is { DataContext: LogTabViewModel shown } view && shown.Title == tab
                    && view.FindControl<ListBox>("Lines")!.Items.OfType<string>().Contains(line),
                $"the {tab} tab to show its line");
        }
        Assert.Equal(0, logs.LogTabs.SelectMany(t => t.Logs).Count(l => l.Contains(AppEngine.Secret, StringComparison.Ordinal)));

        // A closed window lets go of the view model, so its list stops listening to the log lines the Engine keeps sending.
        ListBox lines = Ui.Find<LogTabView>(window)!.FindControl<ListBox>("Lines")!;
        window.Close();
        await Ui.PumpUntilAsync(() => lines.ItemsSource is null, "the closed window's list to let go of the log");
    }

    /// <remarks>A log tab, a hotkey and CoreBots' tabs come from their panel's view model rather than the container.</remarks>
    private object Resolve(Type type) =>
        type == typeof(LogTabViewModel) ? app.Get<IEnumerable<LogTabViewModel>>().First()
        : type == typeof(HotKeyItemViewModel) ? app.Get<HotKeysViewModel>().HotKeys.First()
        : app.Get<CoreBotsViewModel>().CoreBotsTabs.Select(t => t.Content).FirstOrDefault(c => c.GetType() == type)
            ?? app.Engine.Services.GetService(type)
            // The Skua Manager's come from its own container, in its own process.
            ?? _manager.Value.GetRequiredService(type);

    /// <remarks>Its folder is in the sandbox, which the fixture deletes.</remarks>
    private readonly Lazy<ServiceProvider> _manager = new(() =>
        new ServiceCollection()
            .AddManagerServices(Directory.CreateDirectory(Path.Combine(AppEngine.SkuaDir, "manager-" + Guid.NewGuid().ToString("N")[..8])).FullName)
            .BuildServiceProvider());

    private static IEnumerable<MenuItem> Leaves(IEnumerable<MenuItem> items) =>
        items.SelectMany(i => i.Items.Count > 0 ? Leaves(i.Items.OfType<MenuItem>()) : [i]);

    /// <summary>Collects Avalonia's binding warnings and errors while it lives.</summary>
    internal sealed class BindingErrors : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;

        public BindingErrors() => Logger.Sink = this;

        public List<string> Lines { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                lock (Lines)
                    Lines.Add($"{level} {source}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
            }
        }

        public void Dispose() => Logger.Sink = _previous;
    }
}
