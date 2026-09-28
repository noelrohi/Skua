using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's themes on Avalonia's Fluent theme, the counterpart of <c>Skua.WPF</c>'s Material Design <c>ThemeService</c>: the theme's light or
/// dark base is the app's theme variant, and its primary colour and the text on it are Fluent's accent colours. Both are the app's, so
/// every open window restyles at once.
/// </summary>
/// <remarks>
/// <para>
/// Themes persist as on Windows, in the same settings: choosing one saves it as <c>CurrentTheme</c>, and saving one adds it to
/// <c>UserThemes</c>; editing the colours or the base changes only the app until then. The colours are Avalonia <see cref="Color"/>s, as
/// <see cref="IThemeService"/> passes them as objects.
/// </para>
/// <para>
/// Colour adjustment stands in for Material Design's: it lightens (on dark) or darkens (on light) the primary colour until it reaches the
/// desired contrast ratio with the window's background, and makes the text on it black or white when its own colour falls short of it.
/// The contrast setting spreads the accent's lighter and darker shades.
/// </para>
/// </remarks>
public sealed class AvaloniaThemeService : ObservableObject, IThemeService
{
    private const string CurrentThemeKey = "CurrentTheme";
    private const string UserThemesKey = "UserThemes";
    private const string DefaultThemesKey = "DefaultThemes";

    /// <summary>The Windows client's first default, for settings that hold none.</summary>
    private const string SkuaDefault = "Skua,Dark,#FF607D8B,#FF607D8B,#FF000000,#FF000000,true,4.5,Medium,All";

    /// <summary>Fluent's window backgrounds, which the adjusted primary colour contrasts with.</summary>
    private static readonly Color DarkBackground = Color.Parse("#FF202020");
    private static readonly Color LightBackground = Color.Parse("#FFF3F3F3");

    private readonly ISettingsService _settings;
    private readonly List<SkuaTheme> _defaults;
    private readonly List<SkuaTheme> _userThemes;

    /// <summary>The accent colours this service puts in the app's resources, where they win over Fluent's own.</summary>
    private ResourceDictionary? _resources;

    private Color _primary;
    private Color _secondary;
    private Color _primaryForeground;
    private Color _secondaryForeground;

    public AvaloniaThemeService(ISettingsService settings)
    {
        _settings = settings;
        _defaults = Read(_settings.Get<StringCollection>(DefaultThemesKey));
        if (_defaults.Count == 0)
            _defaults.Add(SkuaTheme.Parse(SkuaDefault)!);
        _userThemes = Read(_settings.Get<StringCollection>(UserThemesKey));
        Load(SkuaTheme.Parse(_settings.Get<string>(CurrentThemeKey)) ?? _defaults[0]);
        Apply();
    }

    public event ThemeChangedEventHandler? ThemeChanged;

    public event SchemeChangedEventHandler? SchemeChanged;

    public List<object> Presets => [.. _defaults];

    public List<object> UserThemes => [.. _userThemes];

    /// <summary>The theme last chosen or saved.</summary>
    public SkuaTheme CurrentTheme { get; private set; } = null!;

    /// <summary>The primary colour as chosen, before any adjustment.</summary>
    public Color PrimaryColor => _primary;

    /// <summary>The colour of text on the primary colour as chosen, before any adjustment.</summary>
    public Color PrimaryForegroundColor => _primaryForeground;

    /// <summary>The accent colour the app shows now: the primary colour, adjusted when colour adjustment is on.</summary>
    public Color Accent => Adjusts(ThemeColorSelection.Primary) ? Contrasting(_primary, IsDarkTheme ? DarkBackground : LightBackground) : _primary;

    /// <summary>The colour of text on the accent the app shows now.</summary>
    public Color AccentForeground
    {
        get
        {
            Color accent = Accent;
            if (!IsColorAdjusted || ContrastRatio(_primaryForeground, accent) >= DesiredContrastRatio)
                return _primaryForeground;
            return ContrastRatio(Colors.Black, accent) >= ContrastRatio(Colors.White, accent) ? Colors.Black : Colors.White;
        }
    }

    public IEnumerable<object> ColorSelectionValues => Enum.GetValues<ThemeColorSelection>().Cast<object>();

    private object _colorSelectionValue = ThemeColorSelection.All;

    public object ColorSelectionValue
    {
        get => _colorSelectionValue;
        set
        {
            if (value is ThemeColorSelection && SetProperty(ref _colorSelectionValue, value))
                Apply();
        }
    }

    public IEnumerable<object> ContrastValues => Enum.GetValues<ThemeContrast>().Cast<object>();

    private object _contrastValue = ThemeContrast.Medium;

    public object ContrastValue
    {
        get => _contrastValue;
        set
        {
            if (value is ThemeContrast && SetProperty(ref _contrastValue, value))
                Apply();
        }
    }

    private float _desiredContrastRatio = 4.5f;

    public float DesiredContrastRatio
    {
        get => _desiredContrastRatio;
        set
        {
            if (SetProperty(ref _desiredContrastRatio, Math.Clamp(value, 1f, 21f)))
                Apply();
        }
    }

    private bool _isColorAdjusted;

    public bool IsColorAdjusted
    {
        get => _isColorAdjusted;
        set
        {
            if (SetProperty(ref _isColorAdjusted, value))
                Apply();
        }
    }

    private bool _isDarkTheme;

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
                Apply();
        }
    }

    private ColorScheme _activeScheme;

    public ColorScheme ActiveScheme
    {
        get => _activeScheme;
        set => SetProperty(ref _activeScheme, value);
    }

    private object? _selectedColor;

    /// <summary>The colour the editor shows for the active scheme; a new one becomes that scheme's colour, as on Windows.</summary>
    public object? SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (Equals(_selectedColor, value))
                return;
            _selectedColor = value;
            OnPropertyChanged();
            if (value is Color color && color != SchemeColor(ActiveScheme))
                ChangeCustomColor(color);
        }
    }

    public void ApplyBaseTheme(bool isDark) => IsDarkTheme = isDark;

    public void ChangeCustomColor(object? obj)
    {
        if (obj is not Color color)
            return;
        switch (ActiveScheme)
        {
            case ColorScheme.Primary:
                _primary = color;
                break;
            case ColorScheme.Secondary:
                _secondary = color;
                break;
            case ColorScheme.PrimaryForeground:
                _primaryForeground = color;
                break;
            case ColorScheme.SecondaryForeground:
                _secondaryForeground = color;
                break;
        }
        Apply();
    }

    public void ChangeScheme(ColorScheme scheme)
    {
        ActiveScheme = scheme;
        SelectedColor = SchemeColor(scheme);
        SchemeChanged?.Invoke(scheme, SchemeColor(scheme));
    }

    public void SaveTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        SkuaTheme theme = Snapshot(name.Trim());
        CurrentTheme = theme;
        int index = _userThemes.IndexOf(theme);
        if (index >= 0)
            _userThemes[index] = theme;
        else
            _userThemes.Add(theme);
        _settings.Set(CurrentThemeKey, theme.Format());
        SaveUserThemes();
    }

    public void SetCurrentTheme(object? theme)
    {
        if (theme is not SkuaTheme chosen)
            return;
        Load(chosen);
        Apply();
        _settings.Set(CurrentThemeKey, chosen.Format());
        ThemeChanged?.Invoke(chosen);
    }

    public void RemoveTheme(object? theme)
    {
        if (theme is not SkuaTheme removed || !_userThemes.Remove(removed))
            return;
        if (CurrentTheme.Equals(removed))
            SetCurrentTheme(_defaults[0]);
        SaveUserThemes();
    }

    private void SaveUserThemes()
    {
        StringCollection themes = [];
        foreach (SkuaTheme theme in _userThemes)
            themes.Add(theme.Format());
        _settings.Set(UserThemesKey, themes);
        OnPropertyChanged(nameof(UserThemes));
    }

    private void Load(SkuaTheme theme)
    {
        CurrentTheme = theme;
        _primary = theme.Primary;
        _secondary = theme.Secondary;
        _primaryForeground = theme.PrimaryForeground;
        _secondaryForeground = theme.SecondaryForeground;
        // Each setter applies the theme, which the caller does once all are set.
        SetProperty(ref _isDarkTheme, theme.IsDark, nameof(IsDarkTheme));
        SetProperty(ref _isColorAdjusted, theme.UseColorAdjustment, nameof(IsColorAdjusted));
        SetProperty(ref _desiredContrastRatio, theme.ContrastRatio, nameof(DesiredContrastRatio));
        SetProperty(ref _contrastValue, theme.Contrast, nameof(ContrastValue));
        SetProperty(ref _colorSelectionValue, theme.Colors, nameof(ColorSelectionValue));
        ActiveScheme = ColorScheme.Primary;
        _selectedColor = _primary;
        OnPropertyChanged(nameof(SelectedColor));
    }

    private SkuaTheme Snapshot(string name) => new()
    {
        Name = name,
        IsDark = IsDarkTheme,
        Primary = _primary,
        Secondary = _secondary,
        PrimaryForeground = _primaryForeground,
        SecondaryForeground = _secondaryForeground,
        UseColorAdjustment = IsColorAdjusted,
        ContrastRatio = DesiredContrastRatio,
        Contrast = (ThemeContrast)ContrastValue,
        Colors = (ThemeColorSelection)ColorSelectionValue,
    };

    private Color SchemeColor(ColorScheme scheme) => scheme switch
    {
        ColorScheme.Secondary => _secondary,
        ColorScheme.PrimaryForeground => _primaryForeground,
        ColorScheme.SecondaryForeground => _secondaryForeground,
        _ => _primary,
    };

    private bool Adjusts(ThemeColorSelection colors) =>
        IsColorAdjusted && ((ThemeColorSelection)ColorSelectionValue == colors || (ThemeColorSelection)ColorSelectionValue == ThemeColorSelection.All);

    /// <summary>Puts the theme in the app's resources, on the UI thread: its base, and its accent with the shades and text Fluent draws with.</summary>
    private void Apply()
    {
        bool isDark = IsDarkTheme;
        Color accent = Accent;
        Color foreground = AccentForeground;
        double step = (ThemeContrast)ContrastValue switch
        {
            ThemeContrast.Low => 0.1,
            ThemeContrast.High => 0.2,
            _ => 0.15,
        };
        OnPropertyChanged(nameof(PrimaryColor));
        OnPropertyChanged(nameof(PrimaryForegroundColor));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(AccentForeground));
        UiThread.Post(() =>
        {
            if (Application.Current is not { } app)
                return;
            ResourceDictionary resources = new()
            {
                ["SystemAccentColor"] = accent,
                ["TextOnAccentFillColorPrimary"] = foreground,
                ["AccentButtonForeground"] = new SolidColorBrush(foreground),
                ["AccentButtonForegroundPointerOver"] = new SolidColorBrush(foreground),
                ["AccentButtonForegroundPressed"] = new SolidColorBrush(foreground),
            };
            for (int i = 1; i <= 3; i++)
            {
                resources[$"SystemAccentColorLight{i}"] = Mix(accent, Colors.White, step * i);
                resources[$"SystemAccentColorDark{i}"] = Mix(accent, Colors.Black, step * i);
            }
            if (_resources is not null)
                app.Resources.MergedDictionaries.Remove(_resources);
            app.Resources.MergedDictionaries.Add(resources);
            _resources = resources;
            app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        });
    }

    private static List<SkuaTheme> Read(StringCollection? themes) =>
        themes is null ? [] : themes.Cast<string?>().Select(SkuaTheme.Parse).OfType<SkuaTheme>().Distinct().ToList();

    /// <summary><paramref name="color"/>, moved toward white on a dark background or black on a light one until it contrasts enough with it.</summary>
    private Color Contrasting(Color color, Color background)
    {
        Color toward = background == DarkBackground ? Colors.White : Colors.Black;
        Color adjusted = color;
        for (int i = 1; i <= 20 && ContrastRatio(adjusted, background) < DesiredContrastRatio; i++)
            adjusted = Mix(color, toward, i * 0.05);
        return adjusted;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>WCAG's contrast ratio of two colours, from 1 to 21.</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color) => (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
