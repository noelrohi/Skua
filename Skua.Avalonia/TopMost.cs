using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;

namespace Skua.Avalonia;

/// <summary>
/// Top Most, the option to keep a window above the others, which <c>Skua.WPF</c>'s <c>CustomWindow</c> gives each window in its title bar's
/// menu. In the Mac App it is a checked item in each window's Window menu, and it is saved by window, so the window opens on top again.
/// </summary>
public sealed class TopMost
{
    public const string Header = "Top Most";

    /// <summary>The name the main window's option is saved under.</summary>
    public const string MainWindow = "Main";

    private readonly ISettingsService _settings;

    public TopMost(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>The name a window showing <paramref name="viewModel"/> saves its option under: the view model's type, as each shows one.</summary>
    public static string NameOf(object viewModel) => viewModel.GetType().Name;

    /// <summary>Whether the window named <paramref name="name"/> was left on top.</summary>
    public bool IsOn(string name) => Saved().Contains(name);

    /// <summary>
    /// Puts <paramref name="window"/> on top as it was left, and returns the Window menu with its Top Most item, which toggles and saves it.
    /// </summary>
    public NativeMenuItem Menu(Window window, string name)
    {
        window.Topmost = IsOn(name);
        NativeMenuItem item = new(Header)
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = window.Topmost,
            ToolTip = "Keeps this window above the others.",
        };
        item.Command = new RelayCommand(() => item.IsChecked = Set(window, name, !window.Topmost));
        return new NativeMenuItem("Window") { Menu = new NativeMenu { Items = { item } } };
    }

    /// <summary>Puts <paramref name="window"/> on top, or not, and saves it for the windows named <paramref name="name"/>; returns <paramref name="on"/>.</summary>
    public bool Set(Window window, string name, bool on)
    {
        window.Topmost = on;
        List<string> saved = Saved();
        if (on == saved.Contains(name))
            return on;
        if (on)
            saved.Add(name);
        else
            saved.Remove(name);
        _settings.Set(AppSettingsService.TopMostKey, saved);
        return on;
    }

    private List<string> Saved() => [.. _settings.Get<List<string>>(AppSettingsService.TopMostKey) ?? []];
}
