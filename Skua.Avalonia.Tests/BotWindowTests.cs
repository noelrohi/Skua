using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Avalonia.Views.Helpers;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>The Bot Window, the main menu's +: every panel in one window, over the app's Engine playing the simulated game.</summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine and its game.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class BotWindowTests(AppEngine app)
{
    [AvaloniaFact]
    public async Task The_plus_button_and_the_menu_bar_item_open_the_Bot_Window_listing_every_panel_and_showing_the_selected_one()
    {
        MainMenuViewModel mainMenu = app.Get<MainMenuViewModel>();
        List<BotControlViewModelBase> panels = [.. app.Get<IEnumerable<BotControlViewModelBase>>()];
        HelpersBar bar = new(mainMenu);
        Window main = new() { Width = 958, Height = 400, Content = bar };
        List<Window> created = [];
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        Action<Window>? previous = windows.WindowCreated;
        windows.WindowCreated = created.Add;
        try
        {
            main.Show();
            Assert.Equal(["Auto", "Jump", "+"], bar.Children.OfType<Button>().Select(Ui.Text));
            Ui.Click(bar.BotWindowButton);
            HostWindow fromButton = await BotWindowAsync(created, 1);

            NativeMenuItem windowMenu = MainMenus.WindowMenu(mainMenu);
            Assert.Equal(MainMenus.WindowHeader, windowMenu.Header);
            NativeMenuItem item = windowMenu.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == MainMenus.BotWindowHeader);
            item.Command!.Execute(null);
            HostWindow fromMenu = await BotWindowAsync(created, 2);

            // Each is a Bot Window of its own, over the same panels, as on Windows.
            Assert.NotSame(fromButton.DataContext, fromMenu.DataContext);
            foreach (HostWindow window in new[] { fromButton, fromMenu })
            {
                BotWindowView view = Ui.Find<BotWindowView>(window)!;
                Assert.Equal(panels, view.FindControl<ListBox>("ViewsList")!.Items.OfType<BotControlViewModelBase>());
                Assert.Same(panels[0], view.FindControl<ListBox>("ViewsList")!.SelectedItem);
                await Ui.PumpUntilAsync(() => Ui.Find<ScriptLoaderView>(window) is { DataContext: ScriptLoaderViewModel }, "the first panel's view");
                Assert.Equal(panels[0].Title, window.Title);
                Assert.Equal((800d, 450d), (window.Width, window.Height));
            }

            // A window's own Window menu (its Top Most) gets the item too, after its own.
            NativeMenuItem topMost = new(MainMenus.WindowHeader) { Menu = new NativeMenu { Items = { new NativeMenuItem(TopMost.Header) } } };
            Assert.Same(topMost, MainMenus.WindowMenu(mainMenu, topMost));
            Assert.Equal([TopMost.Header, MainMenus.BotWindowHeader],
                topMost.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header));
        }
        finally
        {
            windows.WindowCreated = previous;
            foreach (Window window in created)
                window.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public async Task Search_filters_by_title_and_Home_Previous_and_Next_move_through_every_panel_with_no_binding_errors()
    {
        List<BotControlViewModelBase> panels = [.. app.Get<IEnumerable<BotControlViewModelBase>>()];
        using PanelTests.BindingErrors errors = new();
        (HostWindow window, BotWindowView view, BotWindowViewModel model) = await OpenAsync();
        try
        {
            ListBox list = view.FindControl<ListBox>("ViewsList")!;
            Button previous = view.FindControl<Button>("Previous")!, next = view.FindControl<Button>("Next")!, home = view.FindControl<Button>("Home")!;
            Assert.False(previous.IsEffectivelyEnabled);

            // Next walks every panel to the last, showing each one's view in turn, and is disabled there.
            for (int i = 1; i < panels.Count; i++)
            {
                Ui.Click(next);
                await ShowsAsync(window, view, panels[i]);
                Assert.Same(panels[i], list.SelectedItem);
            }
            Assert.False(next.IsEffectivelyEnabled);
            Assert.True(previous.IsEffectivelyEnabled);

            Ui.Click(previous);
            await ShowsAsync(window, view, panels[^2]);
            Ui.Click(home);
            await ShowsAsync(window, view, panels[0]);
            Assert.False(previous.IsEffectivelyEnabled);
            Assert.Equal(0, model.SelectedIndex);

            // The search keeps the panels whose title has the text; picking one shows it, and moving on goes from its place among them all.
            view.FindControl<TextBox>("SearchBox")!.Text = "packet";
            List<BotControlViewModelBase> packets = panels.Where(p => p.Title.Contains("Packet", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, packets.Count);
            await Ui.PumpUntilAsync(() => list.Items.OfType<BotControlViewModelBase>().SequenceEqual(packets), "the search to filter the list");
            Assert.Null(list.SelectedItem);
            Assert.Same(panels[0], model.SelectedItem);
            list.SelectedItem = packets[1];
            await ShowsAsync(window, view, packets[1]);
            Assert.Equal(panels.IndexOf(packets[1]), model.SelectedIndex);
            Ui.Click(next);
            await ShowsAsync(window, view, panels[panels.IndexOf(packets[1]) + 1]);

            view.FindControl<TextBox>("SearchBox")!.Text = "";
            await Ui.PumpUntilAsync(() => list.Items.Count == panels.Count && ReferenceEquals(list.SelectedItem, model.SelectedItem), "the whole list, selected");
            Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Jump_inside_the_Bot_Window_moves_the_player()
    {
        HelpersTests helpers = new(app);
        await helpers.LogInAsync();
        await helpers.InBattleonAsync();
        (HostWindow window, BotWindowView view, BotWindowViewModel model) = await OpenAsync();
        try
        {
            JumpViewModel jumpModel = app.Get<JumpViewModel>();
            model.SelectedItem = jumpModel;
            JumpView jump = (JumpView)await ShowsAsync(window, view, jumpModel);
            ComboBox cells = jump.FindControl<ComboBox>("Cells")!;
            // Jump is a singleton the other Jump tests use too, so this leaves its pad alone and picks a cell other than the one it holds;
            // picking a cell jumps at once, so it restores nothing afterwards either.
            string target = jumpModel.SelectedCell == "r3" ? "r2" : "r3";
            string pad = jumpModel.SelectedPad is { Length: > 0 } selected ? selected : "Left";
            int before = File.ReadAllLines(AppEngine.CallLog).Length;
            cells.IsDropDownOpen = true;
            await Ui.PumpUntilAsync(() => cells.Items.OfType<string>().Contains(target), "battleon's cells");
            cells.SelectedItem = target;
            cells.IsDropDownOpen = false;
            await Ui.PumpUntilAsync(
                () => app.Get<IScriptPlayer>().Cell == target && File.ReadAllLines(AppEngine.CallLog).Skip(before).Contains($"jump {target} {pad}"),
                $"the player to jump to {target}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_panel_stays_active_while_its_own_window_or_the_Bot_Window_shows_it_and_stops_once_neither_does()
    {
        CurrentDropsViewModel drops = app.Get<CurrentDropsViewModel>();
        PacketLoggerViewModel logger = app.Get<PacketLoggerViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        app.Get<MainMenuViewModel>();
        (HostWindow window, BotWindowView view, BotWindowViewModel model) = await OpenAsync();
        HostWindow? own = null;
        try
        {
            // Its own window open, the Bot Window shows it and moves on: it keeps working in its own window.
            windows.ShowManagedWindow("Current Drops");
            await Ui.PumpUntilAsync(() => windows.OpenWindow("Current Drops") is not null, "the Current Drops window");
            own = windows.OpenWindow("Current Drops")!;
            model.SelectedItem = drops;
            await ShowsAsync(window, view, drops);
            model.SelectedItem = logger;
            await ShowsAsync(window, view, logger);
            await Ui.PumpUntilAsync(() => drops.IsActive, "Current Drops to stay active");
            Assert.True(logger.IsActive);

            // The Bot Window shows it, its own window closes: it keeps working in the Bot Window.
            model.SelectedItem = drops;
            await ShowsAsync(window, view, drops);
            Assert.False(logger.IsActive);
            own.Close();
            await Ui.PumpUntilAsync(() => windows.OpenWindow("Current Drops") is null, "the Current Drops window to close");
            Assert.True(drops.IsActive);

            // Closing the Bot Window stops the panel it shows, and lets go of its view model.
            ListBox list = view.FindControl<ListBox>("ViewsList")!;
            window.Close();
            await Ui.PumpUntilAsync(() => !drops.IsActive, "Current Drops to stop");
            Assert.Null(window.DataContext);
            Assert.Null(list.ItemsSource);
        }
        finally
        {
            own?.Close();
            window.Close();
        }
    }

    /// <summary>Opens a Bot Window as the main menu's + does, and returns it once it shows its view.</summary>
    private async Task<(HostWindow, BotWindowView, BotWindowViewModel)> OpenAsync()
    {
        List<Window> created = [];
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        Action<Window>? previous = windows.WindowCreated;
        windows.WindowCreated = created.Add;
        try
        {
            app.Get<MainMenuViewModel>().ShowBotWindowCommand.Execute(null);
            HostWindow window = await BotWindowAsync(created, 1);
            return (window, Ui.Find<BotWindowView>(window)!, (BotWindowViewModel)window.DataContext!);
        }
        catch
        {
            foreach (Window window in created)
                window.Close();
            throw;
        }
        finally
        {
            windows.WindowCreated = previous;
        }
    }

    private static async Task<HostWindow> BotWindowAsync(List<Window> created, int count)
    {
        await Ui.PumpUntilAsync(() => created.Count >= count && Ui.Find<BotWindowView>(created[count - 1]) is not null, "the Bot Window");
        HostWindow window = Assert.IsType<HostWindow>(created[count - 1]);
        Assert.IsType<BotWindowViewModel>(window.DataContext);
        return window;
    }

    /// <summary>Waits for the Bot Window to show <paramref name="panel"/>'s view, titled with it, and returns the view.</summary>
    private static async Task<UserControl> ShowsAsync(HostWindow window, BotWindowView view, BotControlViewModelBase panel)
    {
        ContentControl shown = view.FindControl<ContentControl>("Shown")!;
        await Ui.PumpUntilAsync(
            () => Ui.Find<UserControl>(shown) is { } v && ReferenceEquals(v.DataContext, panel) && window.Title == panel.Title && panel.IsActive,
            $"the {panel.Title} panel");
        return Ui.Find<UserControl>(shown)!;
    }
}
