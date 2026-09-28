using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.Avalonia;

/// <summary>A small modal question with Cancel and a confirming button; <c>ShowDialog&lt;bool&gt;</c> returns true for the confirming one.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirm)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ConfirmButton = new Button { Content = confirm };
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        CancelButton.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { CancelButton, ConfirmButton },
                },
            },
        };
    }

    public Button ConfirmButton { get; }

    /// <summary>The default: Return keeps playing.</summary>
    public Button CancelButton { get; }
}
