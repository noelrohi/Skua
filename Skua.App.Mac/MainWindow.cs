using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Avalonia;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;

namespace Skua.App.Mac;

/// <summary>The main window: the main menu and the login controls, the Game View and the status strip.</summary>
internal sealed class MainWindow : Window
{
    /// <summary>Seconds between the Game View stats lines in the debug log, as for the Game Host's: 60 unless set; 0 turns them off.</summary>
    private const string StatsIntervalVariable = "SKUA_GAMEHOST_STATS_SEC";

    /// <summary>Starts each debug line with the Game View's stats JSON, next to the Game Host's <c>[gamehost] stats</c>.</summary>
    public const string StatsPrefix = "[gameview] stats ";

    /// <summary>
    /// Room for the main menu and the login controls above the Game View and the status strip below it, so the stage opens at its
    /// native size.
    /// </summary>
    private const int BarsHeight = 96;

    public MainWindow(BridgeFlashUtil flash, ILogService log, StatusViewModel status, Menu menu)
    {
        Title = "Skua";
        Width = GameHostLaunch.StageWidth;
        Height = GameHostLaunch.StageHeight + BarsHeight;
        MinWidth = 320;
        MinHeight = 200;
        Background = Brushes.Black;
        GameView view = new(flash);
        view.LiveChanged += reason => log.DebugLog($"[gameview] {reason}");
        LoginBar login = new(status);
        StatusStrip strip = new(status);
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(login, Dock.Top);
        DockPanel.SetDock(strip, Dock.Bottom);
        Content = new DockPanel { Children = { menu, login, strip, view } };
        Opened += (_, _) => view.Focus();

        TimeSpan interval = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(StatsIntervalVariable), out int seconds) && seconds >= 0 ? seconds : 60);
        if (interval > TimeSpan.Zero)
        {
            DispatcherTimer stats = new(DispatcherPriority.Background) { Interval = interval };
            stats.Tick += (_, _) => log.DebugLog(StatsPrefix + view.Stats.TakeJson(interval));
            Opened += (_, _) => stats.Start();
            Closed += (_, _) => stats.Stop();
        }
    }
}
