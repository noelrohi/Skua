using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia;
using Skua.Avalonia.Manager;
using Skua.Avalonia.Services;
using Skua.Control;
using Skua.Core.ViewModels.Manager;

namespace Skua.App.Mac;

/// <summary>
/// The Skua Manager (<c>Skua --manager</c>): the macOS counterpart of <c>Skua.Manager</c>, in its own process, one per data folder. It hosts no
/// Engine: it keeps the accounts, launches a Mac App per account, and lists, shows and stops the running ones (ADR 0006).
/// </summary>
internal sealed class ManagerApp : Application
{
    private readonly ServiceProvider _services;
    private Window? _window;

    private ManagerApp(ServiceProvider services)
    {
        _services = services;
    }

    public static int Run()
    {
        string skuaDir = EngineEndpoint.DefaultSkuaDir();
        using ManagerProcess? claimed = ManagerProcess.TryClaim(skuaDir);
        if (claimed is null)
            return ManagerProcess.ShowRunning(skuaDir) ? EngineExitCodes.Success : EngineExitCodes.AlreadyRunning;

        ServiceProvider services = new ServiceCollection().AddManagerServices(skuaDir).BuildServiceProvider();
        // Core's view models reach some services through Ioc.Default; this process has no Engine configuring it.
        Ioc.Default.ConfigureServices(services);
        ManagerApp app = new(services);
        using PosixSignalRegistration show = PosixSignalRegistration.Create((PosixSignal)AppInstances.ShowSignal, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(app.ShowWindow);
        });
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        });
        AppBuilder.Configure(() => app).UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
        services.GetRequiredService<RunningViewModel>().Dispose();
        return EngineExitCodes.Success;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            ManagerMainViewModel manager = _services.GetRequiredService<ManagerMainViewModel>();
            AvaloniaWindowService windows = _services.GetRequiredService<AvaloniaWindowService>();
            _window = new HostWindow(manager) { Title = manager.Title, Width = 1040, Height = 680, CanResize = true };
            windows.WindowCreated?.Invoke(_window);
            desktop.MainWindow = _window;
            _ = _services.GetRequiredService<RunningViewModel>().WatchAsync();
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        ShowWindow();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }
}
