using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Avalonia;
using Skua.Core.Interfaces;
using Skua.MacOS.GameHost;

namespace Skua.App.Mac;

/// <summary>
/// The main window: the main menu and the login controls, the Game View, the status strip with the Notices beside it, and the sheet of a
/// pending Question over them.
/// </summary>
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

    public MainWindow(BridgeFlashUtil flash, ILogService log, StatusViewModel status, Menu menu, ScriptDialogsViewModel dialogs)
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
        Notices = new NoticesButton(dialogs);
        Sheet = new QuestionSheet(dialogs);
        DockPanel bottom = new() { Background = strip.Background, Children = { Notices, strip } };
        DockPanel.SetDock(Notices, Dock.Right);
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(login, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new Panel { Children = { new DockPanel { Children = { menu, login, bottom, view } }, Sheet } };
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

    public NoticesButton Notices { get; }

    /// <summary>The oldest pending Question, over the window's content.</summary>
    public QuestionSheet Sheet { get; }
}
