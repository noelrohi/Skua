using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Utilities;
using Skua.Avalonia.Services;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The main menu, built from Core's <see cref="MainMenuViewModel"/> as the WPF main menu is: in the window, and in the macOS menu bar.
/// An item opens its managed window, and is disabled while that window has no view (<see cref="AvaloniaWindowService.CanShow"/>);
/// an item with its own command, such as Bank or a plugin's, is always enabled. The Plugins menu follows the plugins' items as they come
/// and go. Given a way to open the Skua Manager, each ends with a Manager menu holding it.
/// </summary>
public static class MainMenus
{
    public const string ManagerHeader = "Skua Manager…";

    /// <summary>The Plugins group, which the WPF window shows as its own menu.</summary>
    public const string PluginsHeader = "Plugins";

    public static Menu InWindow(MainMenuViewModel viewModel, AvaloniaWindowService windows, Action? openManager = null)
    {
        Menu menu = new();
        foreach (MainMenuItemViewModel item in viewModel.MainMenuItems)
            menu.Items.Add(MenuItem(item, windows));
        MenuItem plugins = new() { Header = PluginsHeader };
        Follow(viewModel.Plugins, plugins, items =>
        {
            plugins.Items.Clear();
            foreach (MainMenuItemViewModel item in items)
                plugins.Items.Add(MenuItem(item, windows));
        });
        menu.Items.Add(plugins);
        if (openManager is not null)
        {
            MenuItem manager = new() { Header = ManagerHeader };
            manager.Click += (_, _) => openManager();
            menu.Items.Add(new MenuItem { Header = "Manager", Items = { manager } });
        }
        return menu;
    }

    /// <summary>The item that opens the Skua Manager, for the menu bar and the Dock menu.</summary>
    public static NativeMenuItem ManagerItem(Action openManager)
    {
        NativeMenuItem item = new(ManagerHeader);
        item.Click += (_, _) => openManager();
        return item;
    }

    /// <summary>A menu bar menu per group; an item without a group gets a menu of its own, as a menu bar item can't act by itself.</summary>
    public static NativeMenu Native(MainMenuViewModel viewModel, AvaloniaWindowService windows, Action? openManager = null)
    {
        NativeMenu bar = new();
        foreach (MainMenuItemViewModel item in viewModel.MainMenuItems)
        {
            NativeMenu menu = new();
            if (item.SubItems is { } subItems)
            {
                foreach (MainMenuItemViewModel subItem in subItems)
                    menu.Items.Add(NativeItem(subItem, subItem.Header, windows));
            }
            else
            {
                menu.Items.Add(NativeItem(item, $"Show {item.Header}", windows));
            }
            bar.Items.Add(new NativeMenuItem(item.Header) { Menu = menu });
        }
        NativeMenu plugins = new();
        Follow(viewModel.Plugins, plugins, items =>
        {
            plugins.Items.Clear();
            foreach (MainMenuItemViewModel item in items)
                plugins.Items.Add(NativeItem(item, item.Header, windows));
        });
        bar.Items.Add(new NativeMenuItem(PluginsHeader) { Menu = plugins });
        if (openManager is not null)
            bar.Items.Add(new NativeMenuItem("Manager") { Menu = new NativeMenu { Items = { ManagerItem(openManager) } } });
        return bar;
    }

    /// <summary>
    /// Shows <paramref name="items"/> in <paramref name="menu"/> now and whenever plugins add or remove theirs (on the UI thread, through
    /// <see cref="AppPluginHelper"/>). The collection holds the menu only weakly, so a closed window's menu bar doesn't live on.
    /// </summary>
    private static void Follow(ObservableCollection<MainMenuItemViewModel> items, object menu, Action<List<MainMenuItemViewModel>> show)
    {
        MenuFollower follower = new(items, show);
        follower.Refresh();
        WeakEvents.CollectionChanged.Subscribe(items, follower);
        // The menu keeps its follower alive; the collection's weak subscription doesn't.
        s_followers.Add(menu, follower);
    }

    private static readonly ConditionalWeakTable<object, MenuFollower> s_followers = new();

    private sealed class MenuFollower(ObservableCollection<MainMenuItemViewModel> items, Action<List<MainMenuItemViewModel>> show)
        : IWeakEventSubscriber<NotifyCollectionChangedEventArgs>
    {
        public void Refresh() => show([.. items]);

        public void OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e) => UiThread.Post(Refresh);
    }

    private static MenuItem MenuItem(MainMenuItemViewModel item, AvaloniaWindowService windows)
    {
        MenuItem menuItem = new() { Header = item.Header };
        if (item.SubItems is { } subItems)
        {
            foreach (MainMenuItemViewModel subItem in subItems)
                menuItem.Items.Add(MenuItem(subItem, windows));
        }
        else
        {
            menuItem.Command = item.Command;
            menuItem.IsEnabled = IsEnabled(item, windows);
        }
        return menuItem;
    }

    private static NativeMenuItem NativeItem(MainMenuItemViewModel item, string header, AvaloniaWindowService windows) => new(header)
    {
        Command = item.Command,
        IsEnabled = IsEnabled(item, windows),
    };

    /// <summary>A managed window's item while its window has a view; an item with its own command always.</summary>
    private static bool IsEnabled(MainMenuItemViewModel item, AvaloniaWindowService windows) =>
        !windows.IsManaged(item.Header) || windows.CanShow(item.Header);
}
