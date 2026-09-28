using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views.Helpers;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>The Helpers menu's panels and the main menu's Auto and Jump, over the app's Engine playing the simulated game.</summary>
/// <remarks>In the Game View tests' collection, as they share the one Engine and its game.</remarks>
[Collection(nameof(GameViewTests))]
public sealed class HelpersTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Picking_a_cell_in_Jump_moves_the_player_there()
    {
        await LogInAsync();
        (Window window, HelpersBar bar) = ShowBar();
        JumpView jump = Opened<JumpView>(bar.JumpButton);
        ComboBox cells = jump.FindControl<ComboBox>("Cells")!;

        // Opening the picker lists the map's cells, as on Windows; picking one jumps there, on the left pad.
        cells.IsDropDownOpen = true;
        await Ui.PumpUntilAsync(() => cells.Items.OfType<string>().SequenceEqual(["Enter", "r2", "r3"]), "battleon's cells");
        cells.SelectedItem = "r2";
        cells.IsDropDownOpen = false;

        await Ui.PumpUntilAsync(() => app.Get<IScriptPlayer>().Cell == "r2", "the player to reach r2");
        Assert.Contains("jump r2 Left", GameCalls());
        Assert.Equal("Left", jump.FindControl<ComboBox>("Pads")!.SelectedItem);

        // Current reads the player's cell back; Jump jumps to the cell and pad shown.
        await AppEngine.DoAsync("cell r3");
        Ui.Click(jump.FindControl<Button>("Current")!);
        await Ui.PumpUntilAsync(() => (string?)cells.SelectedItem == "r3", "the current cell");
        jump.FindControl<ComboBox>("Pads")!.SelectedItem = "Right";
        Ui.Click(jump.FindControl<Button>("Jump")!);
        await Ui.PumpUntilAsync(() => GameCalls().Contains("jump r3 Right"), "the jump to r3's right pad");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Auto_attack_starts_and_stops_from_the_Auto_view_and_the_bar_marks_it_running()
    {
        await LogInAsync();
        (Window window, HelpersBar bar) = ShowBar();
        AutoView auto = Opened<AutoView>(bar.AutoButton);
        IScriptAuto engineAuto = app.Get<IScriptAuto>();
        TextBlock mark = Ui.Find<TextBlock>(bar.AutoButton, t => t.Text == "● ")!;
        Assert.False(mark.IsVisible);

        Ui.Click(auto.FindControl<Button>("AutoAttack")!);
        await Ui.PumpUntilAsync(() => engineAuto.IsRunning, "auto attack to start");
        await Ui.PumpUntilAsync(() => auto.FindControl<Button>("Stop")!.IsVisible && !auto.FindControl<Button>("AutoAttack")!.IsVisible && mark.IsVisible,
            "the view and the bar to show it running");

        Ui.Click(auto.FindControl<Button>("Stop")!);
        await Ui.PumpUntilAsync(() => !engineAuto.IsRunning, "auto attack to stop");
        await Ui.PumpUntilAsync(() => !auto.FindControl<Button>("Stop")!.IsVisible && auto.FindControl<Button>("AutoAttack")!.IsVisible && !mark.IsVisible,
            "the view and the bar to show it stopped");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_drop_appears_in_Current_Drops_and_the_search_filters_it()
    {
        await LogInAsync();
        (Window window, CurrentDropsView drops) = await OpenAsync<CurrentDropsView>("Current Drops");
        string id = Random.Shared.Next(1000, 9999).ToString();

        // The game event arrives on the Bridge's thread.
        await AppEngine.DoAsync($"drop {id} 2 Frogzard Scale {id}");
        await AppEngine.DoAsync($"drop {id}1 1 Dragon Egg {id}");
        ListBox list = drops.FindControl<ListBox>("CurrentDropsList")!;
        await Ui.PumpUntilAsync(() => Names(list).Contains($"Frogzard Scale {id}") && Names(list).Contains($"Dragon Egg {id}"), "the drops in the list");

        drops.FindControl<TextBox>("SearchBox")!.Text = $"dragon egg {id}";
        await Ui.PumpUntilAsync(() => Names(list).SequenceEqual([$"Dragon Egg {id}"]), "the search to filter the list");
        drops.FindControl<TextBox>("SearchBox")!.Text = string.Empty;
        await Ui.PumpUntilAsync(() => Names(list).Contains($"Frogzard Scale {id}"), "the list unfiltered");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_Fast_Travel_entry_added_in_the_panel_joins_its_map_and_edits_in_a_dialog()
    {
        await LogInAsync();
        (Window window, FastTravelView panel) = await OpenAsync<FastTravelView>("Fast Travel");
        FastTravelViewModel model = (FastTravelViewModel)panel.DataContext!;
        string name = "Yulgar " + Guid.NewGuid().ToString("N")[..6];
        using PanelTests.BindingErrors errors = new();

        panel.FindControl<Expander>("AddTravel")!.IsExpanded = true;
        FastTravelEditorView editor = await FoundAsync<FastTravelEditorView>(panel);
        editor.FindControl<TextBox>("DescriptionName")!.Text = name;
        editor.FindControl<TextBox>("MapName")!.Text = "yulgar";
        editor.FindControl<TextBox>("Cell")!.Text = "Upstairs";
        editor.FindControl<TextBox>("Pad")!.Text = "Left";
        Ui.Click(panel.FindControl<Button>("Add")!);
        await Ui.PumpUntilAsync(() => TravelButton(panel, name) is not null, "the new travel in the list");

        Ui.Click(TravelButton(panel, name)!);
        await Ui.PumpUntilAsync(() => app.Get<IScriptMap>().Name == "yulgar" && app.Get<IScriptPlayer>().Cell == "Upstairs", "the player in yulgar, Upstairs");
        Assert.Contains("tfer yulgar Upstairs Left", GameCalls());

        // The search shows the travels whose name has it.
        panel.FindControl<TextBox>("SearchBox")!.Text = "no such travel";
        await Ui.PumpUntilAsync(() => TravelButton(panel, name) is null, "the search to hide the travel");
        panel.FindControl<TextBox>("SearchBox")!.Text = name;
        await Ui.PumpUntilAsync(() => panel.Shown.Select(t => t.DescriptionName).SequenceEqual([name]), "the search to show only the travel");

        // Edit shows the edit dialog; Confirm replaces the travel with the edited copy. The UI thread runs the dialog in a nested frame, so
        // the dialog is driven from here through the dispatcher.
        List<Window> dialogs = [];
        app.Get<AvaloniaDialogService>().WindowCreated = dialogs.Add;
        Task edited = Task.Run(async () =>
        {
            while (Dispatcher.UIThread.Invoke(() => dialogs.OfType<DialogWindow>().FirstOrDefault(d => d.IsVisible) is null))
                await Task.Delay(20, Ct);
            Dispatcher.UIThread.Invoke(() =>
            {
                DialogWindow dialog = dialogs.OfType<DialogWindow>().First(d => d.IsVisible);
                Assert.Equal("Edit Fast Travel", dialog.Title);
                FastTravelEditorView dialogEditor = Ui.Find<FastTravelEditorView>(dialog)!;
                Assert.Equal("Upstairs", dialogEditor.FindControl<TextBox>("Cell")!.Text);
                dialogEditor.FindControl<TextBox>("Cell")!.Text = "Room";
                Ui.Click(Ui.Find<Button>(dialog, b => b.Name == "Confirm")!);
            });
        }, Ct);
        Button edit = Ui.Find<Button>(panel, b => b.Content as string == "Edit" && b.DataContext is FastTravelItemViewModel t && t.DescriptionName == name)!;
        Dispatcher.UIThread.Post(() => Ui.Click(edit));
        await Ui.PumpUntilAsync(() => edited.IsCompleted, "the edit dialog to be confirmed");
        await edited;
        await Ui.PumpUntilAsync(() => model.FastTravelItems.Any(t => t.DescriptionName == name && t.Cell == "Room"), "the edited travel");
        Assert.Single(model.FastTravelItems, t => t.DescriptionName == name);

        // Remove takes it out of the list, and the saved list with it.
        Ui.Click(Ui.Find<Button>(panel, b => b.Content as string == "✕" && b.DataContext is FastTravelItemViewModel t && t.DescriptionName == name)!);
        await Ui.PumpUntilAsync(() => model.FastTravelItems.All(t => t.DescriptionName != name), "the travel to be removed");
        Assert.True(errors.Lines.Count == 0, string.Join("\n", errors.Lines));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Runtime_adds_drops_and_quests_removes_them_by_key_and_opens_Notify_Drop()
    {
        (Window window, RuntimeHelpersView runtime) = await OpenAsync<RuntimeHelpersView>("Runtime");
        IScriptDrop drops = app.Get<IScriptDrop>();
        ToPickupDropsView toPickup = Ui.Find<ToPickupDropsView>(runtime)!;
        string drop = "Test Drop " + Guid.NewGuid().ToString("N")[..6];

        // Return in the drop box adds the drop, as the Add button does.
        TextBox input = toPickup.FindControl<TextBox>("AddDropInput")!;
        input.Text = drop;
        input.Focus();
        Press(window, Key.Enter, PhysicalKey.Enter);
        ListBox list = toPickup.FindControl<ListBox>("ToPickupList")!;
        await Ui.PumpUntilAsync(() => drops.ToPickup.Contains(drop) && list.Items.OfType<string>().Contains(drop), "the drop in the list");

        // ⌫ removes the selected drops.
        list.SelectedItem = drop;
        Assert.True(list.ContainerFromItem(drop)!.Focus(), "the drop takes the focus, as a click gives it");
        Press(window, Key.Back, PhysicalKey.Backspace);
        await Ui.PumpUntilAsync(() => !drops.ToPickup.Contains(drop) && !list.Items.OfType<string>().Contains(drop), "the drop to be removed");

        // The quest list adds quest IDs with their reward.
        RegisteredQuestsView quests = Ui.Find<RegisteredQuestsView>(runtime)!;
        quests.FindControl<TextBox>("QuestId")!.Text = "1001";
        Ui.Click(quests.FindControl<Button>("AddQuest")!);
        IScriptQuest engineQuests = app.Get<IScriptQuest>();
        await Ui.PumpUntilAsync(() => quests.FindControl<ListBox>("AutoQuestsList")!.Items.OfType<RegisteredQuestInfo>().Any(q => q.QuestId == 1001), "the quest in the list");
        engineQuests.UnregisterQuests(1001);

        // The bell opens Notify Drop, whose list takes names.
        Ui.Click(Ui.Find<Button>(toPickup, b => b.Content as string == "Notify…")!);
        (Window notifyWindow, NotifyDropView notify) = await ShownAsync<NotifyDropView>("Notify Drop");
        notify.FindControl<TextBox>("AddDropInput")!.Text = "Dragon Egg | Frogzard Scale";
        Ui.Click(notify.FindControl<Button>("AddDrop")!);
        NotifyDropViewModel notifyModel = (NotifyDropViewModel)notify.DataContext!;
        await Ui.PumpUntilAsync(() => notify.FindControl<ListBox>("DropList")!.Items.OfType<string>().SequenceEqual(["Dragon Egg", "Frogzard Scale"]), "the names in the list");
        notifyModel.RemoveAllDropsCommand.Execute(null);
        notifyWindow.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Helpers_menu_items_open_their_panels()
    {
        MainMenuViewModel viewModel = app.Get<MainMenuViewModel>();
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        MenuItem helpers = MainMenus.InWindow(viewModel, windows).Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Helpers");

        foreach ((string key, string title, Type viewModelType) in new[]
        {
            ("Runtime", "Runtime", typeof(RuntimeHelpersViewModel)),
            ("Fast Travel", "Fast Travel", typeof(FastTravelViewModel)),
            ("Current Drops", "Current Drops", typeof(CurrentDropsViewModel)),
        })
        {
            MenuItem item = helpers.Items.OfType<MenuItem>().Single(i => (string)i.Header! == key);
            Assert.True(item.IsEnabled, $"{key} is enabled");
            item.Command!.Execute(null);
            await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
            HostWindow window = windows.OpenWindow(key)!;
            Assert.Equal(title, window.Title);
            Assert.IsType(viewModelType, window.DataContext);
            window.Close();
        }
    }

    /// <summary>Logs the app's Engine in to the simulated game, if a test before hasn't, and waits for the inventory, before which the game refuses transfers.</summary>
    private async Task LogInAsync()
    {
        IScriptPlayer player = app.Get<IScriptPlayer>();
        if (!player.Playing)
        {
            Task login = Task.Run(() => app.Engine.Rpc.LoginAsync("Galanoth", cancellationToken: Ct), Ct);
            await Ui.PumpUntilAsync(() => login.IsCompleted, "the login");
            await login;
        }
        await Ui.PumpUntilAsync(() => player.Playing && app.Get<IScriptInventory>().Items.Count > 0, "the player and the inventory");
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null!);
        window.KeyRelease(key, RawInputModifiers.None, physical, null!);
    }

    private (Window, HelpersBar) ShowBar()
    {
        HelpersBar bar = new(app.Get<MainMenuViewModel>());
        Window window = new() { Width = 958, Height = 400, Content = new DockPanel { Children = { bar } } };
        DockPanel.SetDock(bar, Dock.Top);
        window.Show();
        return (window, bar);
    }

    /// <summary>Clicks the bar's button and returns the view its flyout shows.</summary>
    private static T Opened<T>(Button button) where T : UserControl
    {
        Ui.Click(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.Flyout!.IsOpen, "the flyout is open");
        return Assert.IsType<T>(((Flyout)button.Flyout).Content);
    }

    private async Task<(Window, T)> OpenAsync<T>(string key) where T : global::Avalonia.Visual
    {
        // Core's main menu registers the managed windows as it is made.
        app.Get<MainMenuViewModel>();
        app.Get<AvaloniaWindowService>().ShowManagedWindow(key);
        return await ShownAsync<T>(key);
    }

    private async Task<(Window, T)> ShownAsync<T>(string key) where T : global::Avalonia.Visual
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
        Window window = windows.OpenWindow(key)!;
        return (window, await FoundAsync<T>(window));
    }

    private static async Task<T> FoundAsync<T>(global::Avalonia.Visual root) where T : global::Avalonia.Visual
    {
        await Ui.PumpUntilAsync(() => Ui.Find<T>(root) is not null, $"a {typeof(T).Name}");
        return Ui.Find<T>(root)!;
    }

    private static Button? TravelButton(FastTravelView panel, string name) =>
        Ui.Find<Button>(panel, b => b.Classes.Contains("travel") && b.Content as string == name);

    private static List<string> Names(ListBox list) => list.Items.OfType<Skua.Core.Models.Items.ItemBase>().Select(i => i.Name).ToList();

    /// <summary>What the simulated game noted it did, such as <c>jump r2 Left</c> and <c>tfer yulgar Enter Spawn</c>.</summary>
    private static string[] GameCalls() => File.Exists(AppEngine.CallLog) ? File.ReadAllLines(AppEngine.CallLog) : [];
}
