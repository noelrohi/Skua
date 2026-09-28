using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The generic dialogs a Script or plugin shows with <c>IDialogService.ShowDialog</c>: each opens, returns its answer, and cancels cleanly.
/// The message boxes Core shows itself stay Script Dialogs (<see cref="ScriptDialogTests"/>).
/// </summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine. Every key pressed is released and every dialog closed in a finally.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class GenericDialogTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<Window> _dialogs = [];

    [AvaloniaFact]
    public async Task A_message_box_answers_OK_Yes_or_No_and_closing_it_answers_nothing()
    {
        using PanelTests.BindingErrors errors = new();
        await WithDialogsAsync(async service =>
        {
            Task<bool?> ok = Task.Run(() => service.ShowDialog(new MessageBoxDialogViewModel("The farm is done.", "Farm")), Ct);
            DialogWindow dialog = await DialogAsync<MessageBoxDialogViewModel>();
            Assert.Equal("Farm", dialog.Title);
            Assert.Equal("The farm is done.", Ui.Find<SelectableTextBlock>(dialog, t => t.Name == "Message")!.Text);
            Assert.Equal(["OK"], VisibleButtons(dialog));
            Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Ok")!);
            Assert.True(await ShownAsync(ok));

            Task<bool?> yes = Task.Run(() => service.ShowDialog(new MessageBoxDialogViewModel("Buy it?", "Shop", yesAndNo: true)), Ct);
            dialog = await DialogAsync<MessageBoxDialogViewModel>();
            Assert.Equal(["Yes", "No"], VisibleButtons(dialog));
            Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Yes")!);
            Assert.True(await ShownAsync(yes));

            Task<bool?> no = Task.Run(() => service.ShowDialog(new MessageBoxDialogViewModel("Buy it?", "Shop", yesAndNo: true)), Ct);
            dialog = await DialogAsync<MessageBoxDialogViewModel>();
            Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "No")!);
            Assert.False(await ShownAsync(no));

            // Escape is No, the cancel button, not the hidden OK.
            Task<bool?> escaped = Task.Run(() => service.ShowDialog(new MessageBoxDialogViewModel("Buy it?", "Shop", yesAndNo: true)), Ct);
            dialog = await DialogAsync<MessageBoxDialogViewModel>();
            try
            {
                dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null!);
            }
            finally
            {
                // Escape closes the dialog, which holds no keys once closed.
                if (dialog.IsVisible)
                    dialog.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null!);
            }
            Assert.False(await ShownAsync(escaped));

            Task<bool?> closed = Task.Run(() => service.ShowDialog(new MessageBoxDialogViewModel("Buy it?", "Shop", yesAndNo: true)), Ct);
            (await DialogAsync<MessageBoxDialogViewModel>()).Close();
            Assert.Null(await ShownAsync(closed));
        });
        Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
    }

    [AvaloniaFact]
    public async Task A_custom_dialog_answers_the_button_clicked_and_closing_it_leaves_no_answer()
    {
        using PanelTests.BindingErrors errors = new();
        await WithDialogsAsync(async service =>
        {
            CustomDialogViewModel pick = new("Which class?", "Class", ["Red", "Green", "Blue"]);
            Task<bool?> picked = Task.Run(() => service.ShowDialog(pick), Ct);
            DialogWindow dialog = await DialogAsync<CustomDialogViewModel>();
            Assert.Equal("Class", dialog.Title);
            await Ui.PumpUntilAsync(() => VisibleButtons(dialog).Count == 3, "the choices");
            Assert.Equal(["Red", "Green", "Blue"], VisibleButtons(dialog));
            Ui.Click(Ui.Find<Button>(dialog, b => b.Content as string == "Green")!);
            Assert.True(await ShownAsync(picked));
            Assert.Equal(new DialogResult("Green", 1), pick.Result);

            CustomDialogViewModel cancelled = new("Which class?", "Class", ["Red", "Green"]);
            Task<bool?> closed = Task.Run(() => service.ShowDialog(cancelled), Ct);
            (await DialogAsync<CustomDialogViewModel>()).Close();
            Assert.Null(await ShownAsync(closed));
            Assert.Null(cancelled.Result);
        });
        Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
    }

    [AvaloniaFact]
    public async Task The_input_dialog_cancels_with_false()
    {
        await WithDialogsAsync(async service =>
        {
            InputDialogViewModel input = new("Quantity", "How many to buy?", "Quantity", numericInputOnly: false);
            Task<bool?> cancelled = Task.Run(() => service.ShowDialog(input), Ct);
            DialogWindow dialog = await DialogAsync<InputDialogViewModel>();
            Ui.Find<TextBox>(dialog, t => t.Name == "Input")!.Text = "42";
            Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Cancel")!);
            Assert.False(await ShownAsync(cancelled));
        });
    }

    /// <summary>Runs <paramref name="test"/> with the app's dialog service, collecting its dialogs, and closes any left open.</summary>
    private async Task WithDialogsAsync(Func<IDialogService, Task> test)
    {
        AvaloniaDialogService service = app.Get<AvaloniaDialogService>();
        Action<Window>? created = service.WindowCreated;
        service.WindowCreated = _dialogs.Add;
        try
        {
            await test(service);
        }
        finally
        {
            service.WindowCreated = created;
            foreach (Window dialog in _dialogs.Where(d => d.IsVisible))
                dialog.Close();
        }
    }

    private async Task<DialogWindow> DialogAsync<TViewModel>()
    {
        await Ui.PumpUntilAsync(() => Shown() is { DataContext: TViewModel }, $"the {typeof(TViewModel).Name} dialog");
        return Shown()!;
    }

    private DialogWindow? Shown() => _dialogs.OfType<DialogWindow>().LastOrDefault(d => d.IsVisible);

    private static async Task<bool?> ShownAsync(Task<bool?> shown)
    {
        await Ui.PumpUntilAsync(() => shown.IsCompleted, "ShowDialog to return");
        return await shown;
    }

    private static List<string?> VisibleButtons(Window dialog) =>
        [.. dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(Ui.Text)];
}
