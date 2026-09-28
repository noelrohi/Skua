using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's themes before the Mac App has its own (#81): the app keeps Avalonia's Fluent dark theme, and this reports no themes and
/// changes nothing. Core's main menu makes every panel's view model, and the Themes one needs a theme service.
/// </summary>
public sealed class FixedThemeService : IThemeService
{
    public event ThemeChangedEventHandler? ThemeChanged
    {
        add { }
        remove { }
    }

    public event SchemeChangedEventHandler? SchemeChanged
    {
        add { }
        remove { }
    }

    public List<object> Presets { get; } = [];
    public List<object> UserThemes { get; } = [];
    public IEnumerable<object> ColorSelectionValues { get; } = [];
    public object ColorSelectionValue { get; set; } = "";
    public IEnumerable<object> ContrastValues { get; } = [];
    public object ContrastValue { get; set; } = "";
    public float DesiredContrastRatio { get; set; }
    public bool IsColorAdjusted { get; set; }
    public bool IsDarkTheme { get; set; } = true;
    public object? SelectedColor { get; set; }
    public ColorScheme ActiveScheme { get; set; }

    public void ApplyBaseTheme(bool isDark)
    {
    }

    public void ChangeCustomColor(object? obj)
    {
    }

    public void ChangeScheme(ColorScheme scheme)
    {
    }

    public void SaveTheme(string name)
    {
    }

    public void SetCurrentTheme(object? theme)
    {
    }

    public void RemoveTheme(object? theme)
    {
    }
}

/// <summary>Core's hotkeys before the Mac App has its own (#82): none are bound, so no key is taken from the game.</summary>
public sealed class NoHotKeyService : IHotKeyService
{
    public void Reload()
    {
    }

    public List<T> GetHotKeys<T>() where T : IHotKey, new() => [];

    public HotKey? ParseToHotKey(string keyGesture) => null;
}
