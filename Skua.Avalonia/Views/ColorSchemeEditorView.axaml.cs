using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// Edits the theme's colours: the scheme buttons choose which colour to edit, the primary colour or the text on it, and a hex value or a
/// Material hue sets it. The app restyles as it changes; Save keeps it as a theme.
/// </summary>
public partial class ColorSchemeEditorView : UserControl
{
    /// <summary>Material Design's 500 hues, as the WPF picker's palette, then black and white for text.</summary>
    private static readonly string[] s_hues =
    [
        "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3", "#03A9F4", "#00BCD4", "#009688", "#4CAF50",
        "#8BC34A", "#CDDC39", "#FFEB3B", "#FFC107", "#FF9800", "#FF5722", "#795548", "#9E9E9E", "#607D8B", "#000000", "#FFFFFF",
    ];

    private IThemeService? _service;

    public ColorSchemeEditorView()
    {
        InitializeComponent();
        foreach (string hue in s_hues)
        {
            Color color = Color.Parse(hue);
            Button swatch = new()
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.Parse("#44808080")),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, hue);
            swatch.Click += (_, _) => Pick(color);
            Swatches.Children.Add(swatch);
        }
        HexBox.TextChanged += (_, _) =>
        {
            if (HexBox.IsFocused && Color.TryParse(HexBox.Text?.Trim(), out Color color))
                Pick(color);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
            Follow((DataContext as ColorSchemeEditorViewModel)?.ThemeService);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow((DataContext as ColorSchemeEditorViewModel)?.ThemeService);
    }

    /// <summary>Stops following the theme service, which lives on after the window closes.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Follow(null);
    }

    private void Follow(IThemeService? service)
    {
        if (_service is INotifyPropertyChanged old)
            old.PropertyChanged -= OnThemeChanged;
        _service = service;
        if (_service is INotifyPropertyChanged followed)
            followed.PropertyChanged += OnThemeChanged;
        Show();
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => UiThread.Post(Show);

    private void Pick(Color color)
    {
        if (_service is { } service)
            service.SelectedColor = color;
    }

    private void Show()
    {
        if (_service is null)
            return;
        if (_service.SelectedColor is Color selected)
        {
            Preview.Background = new SolidColorBrush(selected);
            // While typing, a shorter form of the same colour stays as typed.
            if (!(HexBox.IsFocused && Color.TryParse(HexBox.Text?.Trim(), out Color typed) && typed == selected))
                HexBox.Text = SkuaTheme.Hex(selected);
        }
        if (_service is AvaloniaThemeService theme)
        {
            IBrush accent = new SolidColorBrush(theme.Accent);
            IBrush text = new SolidColorBrush(theme.AccentForeground);
            PrimarySwatch.Background = accent;
            ForegroundSwatch.Background = accent;
            PrimaryHex.Foreground = text;
            ForegroundHex.Foreground = text;
            PrimaryHex.Text = SkuaTheme.Hex(theme.PrimaryColor);
            ForegroundHex.Text = SkuaTheme.Hex(theme.PrimaryForegroundColor);
            PrimaryScheme.BorderBrush = _service.ActiveScheme == ColorScheme.Primary ? accent : Brushes.Transparent;
            ForegroundScheme.BorderBrush = _service.ActiveScheme == ColorScheme.PrimaryForeground ? accent : Brushes.Transparent;
        }
    }
}
