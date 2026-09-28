using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Skua.Avalonia;

/// <summary>The app's windows, for services that need one to show a dialog or reach the clipboard.</summary>
internal static class Windows
{
    /// <summary>The active window, else the main window, else any open one; null before the app has a window.</summary>
    public static Window? Active()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
    }
}
