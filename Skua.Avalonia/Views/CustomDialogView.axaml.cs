using Avalonia.Controls;
using Avalonia.Interactivity;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// A message with the view model's buttons: a click sets the view model's <c>Result</c> to that button, as on Windows, and closes the dialog
/// with true. Closing it any other way leaves <c>Result</c> null, which the caller takes as <see cref="DialogResult.Cancelled"/>.
/// </summary>
/// <remarks>
/// Core's message boxes stay Script Dialogs of the Engine's broker (ADR 0006); this view is for a Script or plugin that shows the view model
/// itself.
/// </remarks>
public partial class CustomDialogView : UserControl
{
    public CustomDialogView()
    {
        InitializeComponent();
        Choices.AddHandler(Button.ClickEvent, OnChoice);
    }

    private void OnChoice(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Content: string text } || DataContext is not CustomDialogViewModel viewModel)
            return;
        viewModel.Result = new DialogResult(text, viewModel.Buttons.IndexOf(text));
        DialogWindow.Close(this, true);
    }
}
