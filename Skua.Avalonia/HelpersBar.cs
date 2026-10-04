using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Skua.Avalonia.Views.Helpers;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The main window's Auto and Jump buttons, as in <c>Skua.WPF</c>'s <c>MainMenuUserControl</c>: each opens its view below it, and Auto shows a
/// mark while auto attack or hunt runs. The + beside them opens the Bot Window.
/// </summary>
public sealed class HelpersBar : StackPanel
{
    public HelpersBar(MainMenuViewModel mainMenu)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 2;
        Margin = new Thickness(0, 0, 4, 0);

        TextBlock running = new() { Text = "● ", [!IsVisibleProperty] = new Binding("Auto.IsRunning") { Source = mainMenu.AutoViewModel } };
        AutoButton = Opener(new StackPanel { Orientation = Orientation.Horizontal, Children = { running, new TextBlock { Text = "Auto" } } },
            new AutoView { DataContext = mainMenu.AutoViewModel, Width = 400 });
        JumpButton = Opener("Jump", new JumpView { DataContext = mainMenu.JumpViewModel, Width = 300 });
        BotWindowButton = new Button
        {
            Content = "+",
            Padding = new Thickness(8, 2),
            Command = mainMenu.ShowBotWindowCommand,
            [ToolTip.TipProperty] = MainMenus.BotWindowHeader,
        };
        Children.Add(AutoButton);
        Children.Add(JumpButton);
        Children.Add(BotWindowButton);
    }

    public Button AutoButton { get; }

    public Button JumpButton { get; }

    public Button BotWindowButton { get; }

    private static Button Opener(object content, global::Avalonia.Controls.Control view) => new()
    {
        Content = content,
        Padding = new Thickness(8, 2),
        Flyout = new Flyout { Content = view, Placement = PlacementMode.BottomEdgeAlignedRight },
    };
}
