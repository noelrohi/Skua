using System.ComponentModel;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The main window's title, Core's <see cref="MainViewModel.Title"/> as on Windows: <c>Skua - &lt;version&gt;</c>, then <c> : &lt;username&gt;</c>
/// while Application Options' Show Username in Title is on. Unlike Windows, an Engine Name other than <c>default</c> follows it, so the apps
/// the Skua Manager launches can be told apart in the Window menu and with ⌘`.
/// </summary>
public static class MainWindowTitle
{
    /// <summary>The title of the main window of the Engine named <paramref name="engineName"/>, for <see cref="MainViewModel"/>'s <paramref name="title"/>.</summary>
    public static string Of(string title, string engineName) => engineName == EngineName.Default ? title : $"{title} (Engine {engineName})";

    /// <summary>
    /// Records this build's version in the settings, as the Windows app does at its start, then keeps <paramref name="window"/>'s title to
    /// <see cref="MainViewModel"/>'s until the window closes. The view model changes its title from a timer's thread, through Core's
    /// dispatcher; the window's changes on the UI thread whichever thread it comes from.
    /// </summary>
    public static MainViewModel Follow(Window window, IServiceProvider services, string engineName)
    {
        services.GetRequiredService<ISettingsService>().SetApplicationVersion();
        MainViewModel main = services.GetRequiredService<MainViewModel>();
        // Made before the version was recorded, it shows the one it read then.
        main.UpdateTitle();

        void Show() => window.Title = Of(main.Title, engineName);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Title))
                UiThread.Post(Show);
        }
        main.PropertyChanged += OnChanged;
        window.Closed += (_, _) => main.PropertyChanged -= OnChanged;
        Show();
        return main;
    }
}
