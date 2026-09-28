using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Skua.Avalonia.Services;
using Skua.Avalonia.Views;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.ViewModels;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Tools menu's panels and Bank, over the app's Engine with the simulated game: each opens from the main menu as a user opens it.
/// </summary>
[Collection(nameof(GameViewTests))]
public sealed class ToolsTests(AppEngine app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_Loader_loads_a_shop_by_ID_and_the_quests_picked_from_its_searched_list()
    {
        // The quests the list reads from QuestData.json, as the Loader's quest update writes them.
        File.WriteAllText(Path.Combine(AppEngine.SkuaDir, "QuestData.json"), """
            [{"ID":1001,"Name":"Slime Time"},{"ID":1002,"Name":"Chest Hoarder"},{"ID":1003,"Name":"Not Yet"}]
            """);
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            (Window window, LoaderView loader) = await OpenAsync<LoaderView>("Tools", "Loader");
            try
            {
                LoaderViewModel model = (LoaderViewModel)loader.DataContext!;

                loader.FindControl<TextBox>("InputIDs")!.Text = "5";
                loader.FindControl<ComboBox>("Kind")!.SelectedIndex = 0;
                Ui.Click(loader.FindControl<Button>("Load")!);
                await WaitForCallAsync("loadShop 5");

                loader.FindControl<ComboBox>("Kind")!.SelectedIndex = 1;
                loader.FindControl<TextBox>("InputIDs")!.Text = "1001, 1003";
                Ui.Click(loader.FindControl<Button>("Load")!);
                await WaitForCallAsync("showQuests 1001,1003");

                ListBox quests = loader.FindControl<ListBox>("Quests")!;
                await Ui.PumpUntilAsync(() => quests.ItemCount == 3, "the quests read from the file");
                loader.FindControl<TextBox>("SearchBox")!.Text = "chest";
                await Ui.PumpUntilAsync(() => quests.ItemCount == 1, "the search to keep one quest");
                quests.SelectedIndex = 0;
                await Ui.PumpUntilAsync(() => loader.SelectedQuests.Count == 1, "the selection");
                Assert.True(loader.FindControl<Button>("FakeComplete")!.IsEffectivelyEnabled);
                Ui.Click(loader.FindControl<Button>("LoadQuests")!);
                await WaitForCallAsync("showQuests 1002");
                Assert.Equal(3, model.QuestIDs.Count);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            await connection.LogoutAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task The_Grabber_lists_the_inventory_and_shows_the_selected_items_properties()
    {
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            await Ui.PumpUntilAsync(() => app.Get<IScriptInventory>().Items.Count > 0, "the inventory to load");
            (Window window, GrabberView grabber) = await OpenAsync<GrabberView>("Tools", "Grabber");
            try
            {
                GrabberViewModel model = (GrabberViewModel)grabber.DataContext!;
                model.SelectedTab = model.GrabberTabs.Single(t => t.Title == "Inventory");
                GrabberListView list = grabber.FindControl<GrabberListView>("List")!;
                await Ui.PumpUntilAsync(() => list.DataContext == model.SelectedTab, "the Inventory tab");

                Ui.Click(list.FindControl<Button>("Grab")!);
                ListBox items = list.FindControl<ListBox>("Items")!;
                await Ui.PumpUntilAsync(() => items.ItemCount == 3, "the grabbed inventory");
                Assert.Equal(["Default Sword", "Healer", "Treasure Chest"], items.Items.OfType<InventoryItem>().Select(i => i.Name).Order());

                list.FindControl<TextBox>("SearchBox")!.Text = "chest";
                await Ui.PumpUntilAsync(() => items.ItemCount == 1, "the search to keep one item");
                items.SelectedIndex = 0;
                PropertyGrid properties = list.FindControl<PropertyGrid>("Properties")!;
                await Ui.PumpUntilAsync(() => Value(properties, "Name") == "Treasure Chest", "the item's properties");
                Assert.Equal("5", Value(properties, "Quantity"));
                Assert.Equal("1000", Value(properties, "Max Stack"));
                Assert.Contains(PropertyGrid.Rows(items.SelectedItem), r => r is { Name: "Char Item ID", Value: 103 });
                // The inventory's tasks run on the selection.
                Assert.Equal([items.SelectedItem!], list.SelectedItems);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            await connection.LogoutAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task Console_runs_a_line_against_the_game()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        (Window window, ConsoleView console) = await OpenAsync<ConsoleView>("Tools", "Console");
        try
        {

            console.FindControl<TextBox>("Snippet")!.Text = $"Bot.Log(\"console {id}\");";
            Ui.Click(console.FindControl<Button>("Run")!);

            using EngineConnection connection = await ConnectAsync();
            await connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Text == $"console {id}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Bank_in_the_main_menu_opens_the_games_bank_panel()
    {
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            MenuItem bank = Leaf(Menu(), "Bank");
            Assert.True(bank.IsEnabled, "Bank is disabled");

            bank.Command!.Execute(null);

            await WaitForCallAsync("toggleBank open");
            Assert.Equal("Bank", app.Get<Skua.Core.Interfaces.IFlashUtil>().GetGameObject<string>("ui.mcPopup.currentLabel"));
        }
        finally
        {
            await connection.LogoutAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task Stats_shows_the_Scripts_counts_as_they_change_on_other_threads()
    {
        (Window window, ScriptStatsView stats) = await OpenAsync<ScriptStatsView>("Tools", "Stats");
        try
        {
            IScriptBotStats model = app.Get<IScriptBotStats>();
            int kills = model.Kills + 3;

            await Task.Run(() => model.Kills = kills, Ct);

            await Ui.PumpUntilAsync(() => stats.FindControl<TextBlock>("Kills")!.Text == kills.ToString(), "the kills");
            await Ui.PumpUntilAsync(() => stats.FindControl<TextBlock>("Time")!.Text is { Length: 8 }, "the run time");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Junk_Items_lists_the_inventory_and_marks_the_checked_items_as_junk()
    {
        using EngineConnection connection = await ConnectAsync();
        await connection.LoginAsync("Galanoth", cancellationToken: Ct);
        try
        {
            await Ui.PumpUntilAsync(() => app.Get<IScriptInventory>().Items.Count > 0, "the inventory to load");
            (Window window, JunkItemsView junk) = await OpenAsync<JunkItemsView>("Tools", "Junk Items");
            try
            {
                JunkItemsViewModel model = (JunkItemsViewModel)junk.DataContext!;
                ListBox items = junk.FindControl<ListBox>("Items")!;
                await Ui.PumpUntilAsync(() => items.Items.OfType<JunkItemEntry>().Any(e => e.Name == "Treasure Chest"), "the inventory's items", TimeSpan.FromSeconds(60));

                junk.FindControl<TextBox>("SearchBox")!.Text = "treasure";
                await Ui.PumpUntilAsync(() => items.ItemCount == 1, "the search to keep one item");
                JunkItemEntry chest = items.Items.OfType<JunkItemEntry>().Single();
                chest.IsSelected = true;
                try
                {
                    model.MarkAsJunkCommand.Execute(null);
                    await Ui.PumpUntilAsync(() => junk.FindControl<TextBlock>("Total")!.Text == "Total Junk: 1", "the junk count");
                    Assert.True(app.Get<IJunkService>().IsJunk(chest.ID));
                }
                finally
                {
                    model.UnmarkAllJunkCommand.Execute(null);
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            await connection.LogoutAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task Plugins_load_from_the_data_folder_and_one_that_needs_WPF_is_logged_not_fatal()
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        TestPlugins.Write(id);
        MainMenuViewModel _ = app.Get<MainMenuViewModel>();
        IPluginManager plugins = app.Get<IPluginManager>();
        using EngineConnection connection = await ConnectAsync();

        // As the app does once its main menu is up.
        plugins.Initialize();
        try
        {
            Assert.Contains(plugins.Containers, c => c.Plugin.Name == $"Good {id}");
            Assert.Contains(plugins.Containers, c => c.Plugin.Name == $"WPF menu {id}");
            Assert.DoesNotContain(plugins.Containers, c => c.Plugin.Name == $"WPF window {id}");
            string failed = (await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains($"WpfWindow{id}.dll", StringComparison.Ordinal)))[0].Text!;
            Assert.Contains("didn't load", failed);
            Assert.Contains("PresentationFramework", failed);
            Assert.EndsWith("specified. It needs WPF, which only Skua on Windows has.", failed);

            // The plugins' items join the Plugins menu, after View Plugins, which opens the Plugins panel listing the loaded ones.
            Menu menu = Menu();
            MenuItem pluginsMenu = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == MainMenus.PluginsHeader);
            Assert.Equal(["View Plugins", $"Hello {id}", $"Open WPF {id}"], pluginsMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).Where(h => h == "View Plugins" || h.EndsWith(id, StringComparison.Ordinal)));
            Assert.All(pluginsMenu.Items.OfType<MenuItem>(), i => Assert.True(i.IsEnabled, $"{i.Header} is disabled"));
            NativeMenuItem nativePlugins = MainMenus.Native(app.Get<MainMenuViewModel>(), app.Get<AvaloniaWindowService>()).Items.OfType<NativeMenuItem>().Single(i => i.Header == MainMenus.PluginsHeader);
            Assert.Contains(nativePlugins.Menu!.Items.OfType<NativeMenuItem>(), i => i.Header == $"Hello {id}" && i.IsEnabled);

            Leaf(menu, "View Plugins").Command!.Execute(null);
            (Window window, PluginsView view) = await ShownAsync<PluginsView>("Plugins");
            try
            {
                ItemsControl list = view.FindControl<ItemsControl>("Plugins")!;
                await Ui.PumpUntilAsync(() => Names(list).Contains($"Good {id}") && Names(list).Contains($"WPF menu {id}"), "the loaded plugins");
                Assert.DoesNotContain($"WPF window {id}", Names(list));

                Leaf(menu, $"Hello {id}").Command!.Execute(null);
                await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text == $"hello from {id}");
                // Its action needs WPF: the item logs why, and the app runs on.
                Leaf(menu, $"Open WPF {id}").Command!.Execute(null);
                string menuFailed = (await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains($"'Open WPF {id}' failed", StringComparison.Ordinal)))[0].Text!;
                Assert.EndsWith("It needs WPF, which only Skua on Windows has.", menuFailed);

                // Unloading a plugin takes its item out of the menus that are open.
                plugins.Unload($"Good {id}");
                await Ui.PumpUntilAsync(() => !pluginsMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header! == $"Hello {id}"), "the unloaded plugin's item to go");
                await Ui.PumpUntilAsync(() => !Names(list).Contains($"Good {id}"), "the unloaded plugin to leave the panel");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            foreach (IPluginContainer container in plugins.Containers.Where(c => c.Plugin.Name.EndsWith(id, StringComparison.Ordinal)).ToList())
                plugins.Unload(container.Plugin);
            await Ui.PumpUntilAsync(() => !app.Get<MainMenuViewModel>().Plugins.Any(p => p.Header.EndsWith(id, StringComparison.Ordinal)), "the plugins' items to go");
        }
    }

    private static List<string> Names(ItemsControl plugins) =>
        plugins.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    /// <summary>The property grid's value for a property, by its shown name.</summary>
    private static string? Value(PropertyGrid grid, string name)
    {
        TextBlock? label = Ui.Find<TextBlock>(grid, t => t is not SelectableTextBlock && t.Text == name);
        if (label is null)
            return null;
        return grid.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .FirstOrDefault(v => Grid.GetRow(v) == Grid.GetRow(label) && v.Parent == label.Parent)?.Text;
    }

    private Menu Menu() => MainMenus.InWindow(app.Get<MainMenuViewModel>(), app.Get<AvaloniaWindowService>());

    private static MenuItem Leaf(Menu menu, string header) =>
        Leaves(menu.Items.OfType<MenuItem>()).Single(i => (string)i.Header! == header);

    private static IEnumerable<MenuItem> Leaves(IEnumerable<MenuItem> items) =>
        items.SelectMany(i => i.Items.Count > 0 ? Leaves(i.Items.OfType<MenuItem>()) : [i]);

    /// <summary>Opens a panel from its main menu item, as a click does, and returns its window and view.</summary>
    private async Task<(Window, T)> OpenAsync<T>(string group, string item) where T : global::Avalonia.Visual
    {
        MenuItem groupItem = Menu().Items.OfType<MenuItem>().Single(i => (string)i.Header! == group);
        MenuItem leaf = groupItem.Items.OfType<MenuItem>().Single(i => (string)i.Header! == item);
        Assert.True(leaf.IsEnabled, $"{group} → {item} is disabled");
        leaf.Command!.Execute(null);
        return await ShownAsync<T>(item);
    }

    private async Task<(Window, T)> ShownAsync<T>(string key) where T : global::Avalonia.Visual
    {
        AvaloniaWindowService windows = app.Get<AvaloniaWindowService>();
        await Ui.PumpUntilAsync(() => windows.OpenWindow(key) is not null, $"the {key} window");
        Window window = windows.OpenWindow(key)!;
        await Ui.PumpUntilAsync(() => Ui.Find<T>(window) is not null, $"a {typeof(T).Name}");
        return (window, Ui.Find<T>(window)!);
    }

    /// <summary>Waits for the simulated game to record <paramref name="call"/>.</summary>
    private static Task WaitForCallAsync(string call) =>
        Ui.PumpUntilAsync(() => File.Exists(AppEngine.CallLog) && File.ReadAllLines(AppEngine.CallLog).Contains(call), $"the game's '{call}'");

    private static async Task<EngineConnection> ConnectAsync() =>
        await EngineClient.TryConnectAsync(EngineEndpoint.FromEnvironment(), Ct) ?? throw new InvalidOperationException("The app's Engine didn't answer.");
}
