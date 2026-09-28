using Avalonia.Controls;
using Avalonia.Input;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>Asks for a line of text, or a number when the view model says so; Confirm closes the dialog with true.</summary>
/// <remarks>Ports <c>TextBoxOnlyNumbersBehavior</c>: a number-only input takes digits alone.</remarks>
public partial class InputDialogView : UserControl
{
    public InputDialogView()
    {
        InitializeComponent();
        Confirm.Click += (_, _) => DialogWindow.Close(this, true);
        Cancel.Click += (_, _) => DialogWindow.Close(this, false);
        Input.AddHandler(TextInputEvent, OnTextInput, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Hint.IsVisible = DataContext is InputDialogViewModel { DialogHint.Length: > 0 };
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Input.Focus();
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is InputDialogViewModel { NumberOnly: true } && e.Text is { } text && !text.All(char.IsAsciiDigit))
            e.Handled = true;
    }
}
