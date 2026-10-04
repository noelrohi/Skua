using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Engine;
using Skua.MacOS.GameHost;

namespace Skua.App.Mac;

/// <summary>
/// The Mac App: one main window with the Game View over the Engine this process hosts. When a <c>skua-engine</c> holds the name at launch,
/// it first offers to take that Engine over; when its Engine couldn't start for another reason, it shows why.
/// </summary>
/// <remarks>
/// Closing the main window keeps the app and its Engine running, and the Dock icon reopens it; only quitting ends them (ADR 0006).
/// </remarks>
internal sealed class App : Application
{
    private readonly EngineEndpoint _endpoint;
    private readonly AppArguments _arguments;
    private readonly string? _failure;
    private HostedEngine? _engine;
    private Task? _takingOver;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private CloseAndQuit? _closeAndQuit;
    private AppUpdates? _updates;
    private WindowCascade? _cascade;
    private int _quitRequested;
    private bool _shuttingDown;

    /// <param name="engine">The Engine started before Avalonia, or null when it didn't start.</param>
    /// <param name="failure">Why it didn't start, or null when another Engine holds the name, which the app offers to take over.</param>
    /// <param name="arguments">The command line; with an account, the app logs it in once its window shows, as the Skua Manager launches it.</param>
    public App(EngineEndpoint endpoint, AppArguments arguments, HostedEngine? engine, string? failure)
    {
        _endpoint = endpoint;
        _arguments = arguments;
        _engine = engine;
        _failure = failure;
    }

    /// <summary>
    /// How the app hosts its Engine, at launch and after a take-over: with the app's own services and view models, and the account the Skua
    /// Manager launched it for, if any, in place of the Active Account.
    /// </summary>
    public static EngineHostOptions EngineOptions(AppArguments arguments) => new()
    {
        Mode = EngineHostMode.App,
        AccountService = arguments.Account is { } account ? Accounts.ServiceOf(account) : null,
        ConfigureServices = services => services.AddAvaloniaServices(),
    };

    public override void Initialize()
    {
        // The app menu's title; unset, it is "Avalonia Application".
        Name = "Skua";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            // A release only; on the main thread, as Sparkle requires.
            if (AppUpdates.Current() is { } release)
                _updates = AppUpdates.Start(release);
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        _closeAndQuit?.Reopen();
                };
            }

            if (_engine is not null)
                desktop.MainWindow = CreateMainWindow(_engine);
            else if (_failure is null)
                desktop.MainWindow = CreateTakeOverWindow();
            else
                desktop.MainWindow = QuitOnClose(MessageWindow.Create("Skua can't start", _failure));
        }
        base.OnFrameworkInitializationCompleted();
        if (Volatile.Read(ref _quitRequested) == 1)
            Dispatcher.UIThread.Post(Quit);
    }

    /// <summary>The Engine this app hosts once it has started, waiting for a take-over's start in flight. The host stops it after quitting.</summary>
    public async Task<HostedEngine?> EngineAsync()
    {
        if (_takingOver is { } takingOver)
        {
            try
            {
                await takingOver;
            }
            catch (Exception)
            {
                // The take-over window said why.
            }
        }
        return _engine;
    }

    /// <summary>Quits without asking, as on SIGTERM; callable from any thread, before or after Avalonia starts.</summary>
    public void RequestQuit()
    {
        Interlocked.Exchange(ref _quitRequested, 1);
        if (_desktop is not null)
            Dispatcher.UIThread.Post(Quit);
    }

    /// <summary>Shows the main window and brings the app to the front, as the Dock icon does; callable from any thread.</summary>
    public void RequestShow()
    {
        if (_desktop is not null)
            Dispatcher.UIThread.Post(() =>
            {
                if (_closeAndQuit is { } closeAndQuit)
                    closeAndQuit.Reopen();
                else
                    _desktop.MainWindow?.Activate();
            });
    }

    private void Quit()
    {
        if (_closeAndQuit is { } closeAndQuit)
            _ = closeAndQuit.QuitAsync(ask: false);
        else
            Shutdown();
    }

    /// <summary>Ends the lifetime once, however many ways ask for it: shutting down closes the windows whose closing quits.</summary>
    private void Shutdown()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        _desktop?.Shutdown();
    }

    /// <summary>Cmd-Q, or Quit in the Dock menu: once there is an Engine, the close and quit rules decide, asking first while a Script runs.</summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_closeAndQuit is not { IsQuitting: false } closeAndQuit)
        {
            // Avalonia shuts down now, closing the windows, whose closing mustn't shut it down again.
            _shuttingDown = true;
            return;
        }
        e.Cancel = true;
        _ = closeAndQuit.QuitAsync(ask: true);
    }

    private Window CreateMainWindow(HostedEngine engine)
    {
        BridgeFlashUtil flash = engine.Services.GetRequiredService<BridgeFlashUtil>();
        StatusViewModel status = new(engine.Rpc, engine.Endpoint.Name, host: "app");
        engine.StatusChanged += status.Changed;
        // The developer's theme, before any window shows.
        _ = engine.Services.GetRequiredService<IThemeService>();
        // Core's main menu registers the managed windows as it is made.
        MainMenuViewModel mainMenu = engine.Services.GetRequiredService<MainMenuViewModel>();
        AvaloniaWindowService windows = engine.Services.GetRequiredService<AvaloniaWindowService>();
        // Every window carries the menu bar with the Manager item and the Window menu's Bot Window, since macOS shows the key window's;
        // the main window and each panel's also carry their Top Most there.
        TopMost topMost = engine.Services.GetRequiredService<TopMost>();
        NativeMenu MenuBar(Window? window = null, string? name = null)
        {
            NativeMenu bar = MainMenus.Native(mainMenu, windows, OpenManager);
            bar.Items.Add(MainMenus.WindowMenu(mainMenu, window is not null && name is not null ? topMost.Menu(window, name) : null));
            return bar;
        }
        windows.WindowCreated = w => NativeMenu.SetMenu(w, w.DataContext is { } shown ? MenuBar(w, TopMost.NameOf(shown)) : MenuBar());
        // About, Change Logs and the GitHub sign-in sit in the app menu, where macOS puts About.
        NativeMenu.SetMenu(this, AppMenu.Create(engine.Services, windows, _updates is { } updates ? updates.Check : null));
        // The GitHub token signed in with before, from Keychain, as the Windows client reads its saved one at its start.
        _ = Task.Run(() => engine.Services.GetRequiredService<GitHubToken>().LoadAsync(CancellationToken.None));
        // Made here, on the UI thread, where its collections change.
        ScriptDialogsViewModel dialogs = engine.Services.GetRequiredService<ScriptDialogsViewModel>();
        MainWindow window = new(flash, engine.Services.GetRequiredService<ILogService>(), status, new HelpersBar(mainMenu), dialogs);
        // Down and right of the windows of the other apps open on this data folder, such as those the Skua Manager launches.
        _cascade ??= WindowCascade.Claim(_endpoint.SkuaDir);
        _cascade.Place(window);
        // Core's title, with the version and, if the developer chose, the username; and the Engine Name unless it's the default.
        MainWindowTitle.Follow(window, engine.Services, engine.Endpoint.Name);
        window.Notices.WindowOpened = w => NativeMenu.SetMenu(w, MenuBar());
        engine.Services.GetRequiredService<AvaloniaDialogService>().WindowCreated = w => NativeMenu.SetMenu(w, MenuBar());
        bool Frontmost() => _desktop?.Windows.Any(w => w.IsActive) == true;
        // What the app shows by itself waits while another app is frontmost (#142).
        Foreground foreground = engine.Services.GetRequiredService<Foreground>();
        foreground.Frontmost = Frontmost;
        foreground.Notify = MacNotifications.Post;
        _ = new ScriptDialogAlerts(dialogs, window, Frontmost, MacNotifications.Post);
        // As the Windows tray's balloons: a Script's stop or error, and a relogin, while the window isn't in front.
        IScriptPlayer player = engine.Services.GetRequiredService<IScriptPlayer>();
        _ = new ScriptStatusAlerts(window, Frontmost, () => player.Username, MacNotifications.Post);
        // As on Windows, the plugins in the data folder load once the main menu can take their items; one that fails is only logged.
        engine.Services.GetRequiredService<IPluginManager>().Initialize();
        NativeMenu.SetMenu(window, MenuBar(window, TopMost.MainWindow));
        if (NativeDock.GetMenu(this) is null)
            NativeDock.SetMenu(this, new NativeMenu { Items = { MainMenus.ManagerItem(OpenManager) } });
        _closeAndQuit = new CloseAndQuit(window, engine.Rpc, engine.Endpoint.Name, () =>
        {
            // The app goes headless as it quits, before the Engine stops.
            flash.SetLive(false);
            Shutdown();
        });
        // Once the window is first up: launched by the Skua Manager for an account, log it in and start its Script; then the start-up checks,
        // as on Windows, after that Script has started, so the Scripts check leaves its files alone.
        StartUpChecks checks = engine.Services.GetRequiredService<StartUpChecks>();
        ILogService log = engine.Services.GetRequiredService<ILogService>();
        async void OnFirstOpened(object? sender, EventArgs e)
        {
            window.Opened -= OnFirstOpened;
            if (_arguments.Account is not null)
                await status.LaunchAsync(_arguments.Server, _arguments.Script);
            try
            {
                await checks.RunAsync();
            }
            catch (Exception failure)
            {
                log.DebugLog($"Start-up check failed: {failure}");
            }
        }
        window.Opened += OnFirstOpened;
        // Only quitting stops the Engine, but should it stop by itself the window can't go on without it.
        engine.Completion.ContinueWith(_ => RequestQuit(), TaskScheduler.Default);
        return window;
    }

    /// <summary>The menu's and the Dock's "Skua Manager": opens the Manager's own process, or brings it to the front.</summary>
    private void OpenManager()
    {
        try
        {
            ManagerProcess.Open(_endpoint.SkuaDir);
        }
        catch (ControlException e)
        {
            MessageWindow.Create("Skua Manager", e.Message).Show();
        }
    }

    private Window CreateTakeOverWindow()
    {
        Window window = new()
        {
            Title = "Skua",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        TakeOverViewModel model = new(_endpoint, () =>
        {
            _takingOver = StartTakenOverEngineAsync(window);
            return _takingOver;
        });
        model.QuitRequested += Shutdown;
        window.Content = new TakeOverView(model);
        QuitOnClose(window);
        window.Opened += (_, _) => _ = model.LookAsync();
        return window;
    }

    /// <summary>Starts the app's Engine once the name is free, then swaps the take-over window for the main window.</summary>
    /// <remarks>
    /// Unlike a start before Avalonia, this one binds the socket while the UI threads run, so a file one of them creates during the bind gets
    /// the Engine's umask: owner-only, tighter than usual. The window is short and the result harmless for a per-user app.
    /// </remarks>
    private async Task StartTakenOverEngineAsync(Window takeOverWindow)
    {
        _engine = await Task.Run(() => HostedEngine.StartAsync(_endpoint, EngineOptions(_arguments)));
        Window main = CreateMainWindow(_engine);
        if (_desktop is not null)
            _desktop.MainWindow = main;
        main.Show();
        takeOverWindow.Closed -= OnQuitWindowClosed;
        takeOverWindow.Close();
        if (Volatile.Read(ref _quitRequested) == 1)
            Quit();
    }

    /// <summary>Closing a window shown before there is an Engine quits the app.</summary>
    private Window QuitOnClose(Window window)
    {
        window.Closed += OnQuitWindowClosed;
        return window;
    }

    private void OnQuitWindowClosed(object? sender, EventArgs e) => Shutdown();
}
