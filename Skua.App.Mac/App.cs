using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Engine;
using Skua.MacOS.GameHost;

namespace Skua.App.Mac;

/// <summary>The Mac App: one main window with the Game View over the Engine this process hosts, or a message when it couldn't start one.</summary>
internal sealed class App(HostedEngine? engine, string? failure) : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (engine is null)
            {
                desktop.MainWindow = MessageWindow.Create("Skua can't start", failure ?? "The Engine didn't start.");
            }
            else
            {
                BridgeFlashUtil flash = engine.Services.GetRequiredService<BridgeFlashUtil>();
                MainWindow window = new(flash, engine.Services.GetRequiredService<ILogService>());
                desktop.MainWindow = window;
                // The app goes headless as it quits, before the Engine stops.
                desktop.ShutdownRequested += (_, _) => flash.SetLive(false);
                // An Engine that stops by itself (a shutdown request) takes the window with it.
                engine.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(() => desktop.Shutdown()), TaskScheduler.Default);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
