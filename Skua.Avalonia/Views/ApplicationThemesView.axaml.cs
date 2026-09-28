using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Avalonia.Services;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>The theme editor, the defined and saved themes, and the game's backgrounds. A theme's button makes it the app's theme; a saved theme's minus removes it.</summary>
public partial class ApplicationThemesView : UserControl
{
    public ApplicationThemesView()
    {
        InitializeComponent();
        Presets.AddHandler(Button.ClickEvent, OnThemeClicked);
        UserThemes.AddHandler(Button.ClickEvent, OnThemeClicked);
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationThemesViewModel model || e.Source is not Button { DataContext: SkuaTheme theme } button)
            return;
        if (Equals(button.Tag, "remove"))
            model.RemoveThemeCommand.Execute(theme);
        else
            model.SetCurrentThemeCommand.Execute(theme);
    }
}
