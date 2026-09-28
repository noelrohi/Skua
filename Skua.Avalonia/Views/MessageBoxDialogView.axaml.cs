using Avalonia.Controls;

namespace Skua.Avalonia.Views;

/// <summary>
/// A message, with OK, or with Yes and No when the view model asks. Yes and OK close the dialog with true and No with false, so
/// <c>ShowDialog</c> returns the answer; closing it any other way returns null.
/// </summary>
/// <remarks>
/// Core's message boxes stay Script Dialogs of the Engine's broker (ADR 0006); this view is for a Script or plugin that shows the view model
/// itself.
/// </remarks>
public partial class MessageBoxDialogView : UserControl
{
    public MessageBoxDialogView()
    {
        InitializeComponent();
        Ok.Click += (_, _) => DialogWindow.Close(this, true);
        Yes.Click += (_, _) => DialogWindow.Close(this, true);
        No.Click += (_, _) => DialogWindow.Close(this, false);
    }
}
