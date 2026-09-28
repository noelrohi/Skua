using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Skua.MacOS.Services;

namespace Skua.Avalonia.Tests;

/// <summary>The Notices list over a broker of its own, as a user reads and clears it.</summary>
public sealed class NoticesButtonTests
{
    [AvaloniaFact]
    public async Task Clear_in_the_open_list_empties_it_with_a_row_selected_and_the_list_keeps_working()
    {
        // #137: Clear crashed the app. The rows it recycled were built once more with no Notice.
        ScriptDialogBroker broker = new();
        ScriptDialogsViewModel dialogs = new(broker);
        NoticesButton notices = new(dialogs);
        Window window = new() { Width = 958, Height = 646, Content = new DockPanel { Children = { notices } } };
        try
        {
            window.Show();
            for (int i = 1; i <= 3; i++)
                broker.Notice($"Notice {i}", $"Text {i}");
            await Ui.PumpUntilAsync(() => dialogs.Notices.Count == 3, "the Notices in the list");
            Assert.Equal("3", notices.Badge.Text);

            await Ui.ClickAsync(window, notices);
            await Ui.PumpUntilAsync(() => notices.Flyout!.IsOpen && notices.List.ContainerFromIndex(1) is not null, "the list's rows");
            await Ui.ClickAsync(window, notices.List.ContainerFromIndex(1)!);
            Assert.Equal("Notice 2", ((ShownNotice)notices.List.SelectedItem!).Caption);
            await Ui.ClickAsync(window, notices.ClearButton);

            Assert.Empty(dialogs.Notices);
            Assert.Null(notices.List.SelectedItem);
            Assert.True(notices.Flyout!.IsOpen, "Clear closed the list");
            Assert.False(notices.List.IsVisible);
            Assert.False(notices.ClearButton.IsEnabled);
            Assert.False(notices.OpenButton.IsEnabled);
            Assert.False(notices.BadgeBorder.IsVisible);
            Assert.Equal((0, "0"), (dialogs.Unread, notices.Badge.Text));

            // The next Notice lists and counts as the first did.
            notices.Flyout.Hide();
            broker.Notice("After", "Shown after the Clear.");
            await Ui.PumpUntilAsync(() => notices.BadgeBorder.IsVisible, "the badge for the next Notice");
            Assert.Equal("1", notices.Badge.Text);
            await Ui.ClickAsync(window, notices);
            await Ui.PumpUntilAsync(() => notices.List.ContainerFromIndex(0) is ListBoxItem { IsVisible: true }, "the next Notice's row");
            Assert.Equal("After", Assert.Single(dialogs.Notices).Caption);
            Assert.True(notices.ClearButton.IsEnabled);
        }
        finally
        {
            notices.Flyout?.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_Notice_past_the_most_kept_drops_the_oldest_even_while_its_row_is_on_screen()
    {
        // The same recycling as #137's Clear, of the one row the cap removes.
        ScriptDialogBroker broker = new();
        ScriptDialogsViewModel dialogs = new(broker);
        NoticesButton notices = new(dialogs);
        Window window = new() { Width = 958, Height = 646, Content = new DockPanel { Children = { notices } } };
        try
        {
            window.Show();
            for (int i = 1; i <= ScriptDialogsViewModel.MaxNotices; i++)
                broker.Notice($"Notice {i}", $"Text {i}");
            await Ui.PumpUntilAsync(() => dialogs.Notices.Count == ScriptDialogsViewModel.MaxNotices, "the most Notices kept");
            await Ui.ClickAsync(window, notices);
            await Ui.PumpUntilAsync(() => notices.Flyout!.IsOpen && notices.List.ContainerFromIndex(0) is not null, "the list's rows");
            notices.List.ScrollIntoView(ScriptDialogsViewModel.MaxNotices - 1);
            await Ui.PumpUntilAsync(() => notices.List.ContainerFromIndex(ScriptDialogsViewModel.MaxNotices - 1) is { IsVisible: true },
                "the oldest Notice's row");

            broker.Notice("Newest", "One past the most kept.");
            await Ui.PumpUntilAsync(() => dialogs.Notices[0].Caption == "Newest", "the newest Notice");

            Assert.Equal(ScriptDialogsViewModel.MaxNotices, dialogs.Notices.Count);
            Assert.Equal("Notice 2", dialogs.Notices[^1].Caption);
        }
        finally
        {
            notices.Flyout?.Hide();
            window.Close();
        }
    }
}
