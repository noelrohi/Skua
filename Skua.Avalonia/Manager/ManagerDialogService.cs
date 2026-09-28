using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.Avalonia.Manager;

/// <summary>
/// Core's dialogs in the Skua Manager, as modal Avalonia windows: message boxes, and the text and group pickers the account list asks with.
/// The Manager hosts no Engine, so there is no Script Dialog broker to answer them.
/// </summary>
public sealed class ManagerDialogService : IDialogService
{
    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => ShowDialog(viewModel, "Skua Manager");

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => viewModel switch
    {
        InputDialogViewModel input => Ask(title, input),
        SelectGroupDialogViewModel groups => Ask(title, groups),
        _ => null,
    };

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        bool? result = ShowDialog(viewModel);
        callback(viewModel);
        return result;
    }

    public void ShowMessageBox(string message, string caption) => Show(caption, message, "OK");

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo) =>
        yesAndNo ? Show(caption, message, "No", "Yes") == 1 : Show(caption, message, "OK") == 0 ? true : null;

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        int chosen = Show(caption, message, buttons.Length > 0 ? buttons : ["OK"]);
        return chosen < 0 ? DialogResult.Cancelled : new DialogResult(buttons.Length > 0 ? buttons[chosen] : "OK", chosen);
    }

    /// <returns>The chosen button's index, or -1 when the window was closed.</returns>
    private static int Show(string title, string message, params string[] buttons) => UiThread.Wait(() =>
    {
        Window window = Dialog(title);
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            Button button = new() { Content = buttons[i], IsDefault = i == buttons.Length - 1 };
            button.Click += (_, _) => Close(window, index);
            row.Children.Add(button);
        }
        window.Content = Body(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row);
        return RunAsync(window, -1);
    });

    private static bool? Ask(string title, InputDialogViewModel input) => UiThread.Wait(() =>
    {
        Window window = Dialog(string.IsNullOrEmpty(title) ? input.Title : title);
        TextBox text = new() { Text = input.DialogTextInput, PlaceholderText = input.TextBoxHint };
        Button ok = new() { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            input.DialogTextInput = text.Text ?? "";
            Close(window, (bool?)true);
        };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(window, (bool?)false);
        window.Opened += (_, _) => text.Focus();
        window.Content = Body(
            new StackPanel { Spacing = 8, Children = { new TextBlock { Text = input.DialogHint, TextWrapping = TextWrapping.Wrap }, text } },
            Buttons(cancel, ok));
        return RunAsync<bool?>(window, false);
    });

    private static bool? Ask(string title, SelectGroupDialogViewModel groups) => UiThread.Wait(() =>
    {
        Window window = Dialog(title);
        ListBox list = new() { ItemsSource = groups.Groups, MaxHeight = 240, DisplayMemberBinding = new global::Avalonia.Data.Binding(nameof(GroupItemViewModel.Name)) };
        Button ok = new() { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            groups.SelectedGroup = list.SelectedItem as GroupItemViewModel;
            groups.ConfirmCommand.Execute(null);
            Close(window, (bool?)groups.DialogResult);
        };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(window, (bool?)false);
        window.Content = Body(
            groups.Groups.Count == 0 ? new TextBlock { Text = "There are no groups yet; create one first." } : list,
            Buttons(cancel, ok));
        return RunAsync<bool?>(window, false);
    });

    private static Window Dialog(string title) => new()
    {
        Title = title,
        Width = 420,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static StackPanel Body(global::Avalonia.Controls.Control content, global::Avalonia.Controls.Control buttons) => new() { Margin = new Thickness(20), Spacing = 16, Children = { content, buttons } };

    private static StackPanel Buttons(params Button[] buttons)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.AddRange(buttons);
        return row;
    }

    /// <summary>Closes the dialog with <paramref name="result"/>, which <see cref="RunAsync"/> returns.</summary>
    private static void Close<T>(Window window, T result)
    {
        window.Tag = new Answer<T>(result);
        window.Close();
    }

    /// <summary>Modal over the active window, else a plain window; either way its closing ends the wait.</summary>
    private static async Task<T> RunAsync<T>(Window window, T closed)
    {
        TaskCompletionSource<T> done = new();
        window.Closed += (_, _) => done.TrySetResult(window.Tag is Answer<T> answer ? answer.Value : closed);
        if (Windows.Active() is { } owner && owner != window)
            _ = window.ShowDialog(owner);
        else
            window.Show();
        return await done.Task;
    }

    private sealed record Answer<T>(T Value);
}
