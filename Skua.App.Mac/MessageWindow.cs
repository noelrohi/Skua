using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.App.Mac;

/// <summary>A small window with a message and a Quit button, for when the app can't start its Engine.</summary>
internal static class MessageWindow
{
    public static Window Create(string title, string message)
    {
        Window window = new()
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        Button quit = new() { Content = "Quit", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        quit.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                quit,
            },
        };
        return window;
    }
}
