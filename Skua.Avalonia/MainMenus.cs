using Avalonia.Controls;
using Skua.Avalonia.Services;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The main menu, built from Core's <see cref="MainMenuViewModel"/> as the WPF main menu is: in the window, and in the macOS menu bar.
/// An item opens its managed window, and is disabled while that window has no view (<see cref="AvaloniaWindowService.CanShow"/>);
/// items with their own command, such as Bank and the plugins, stay disabled until their tickets. Given a way to open the Skua Manager, each
/// ends with a Manager menu holding it.
/// </summary>
public static class MainMenus
{
    public const string ManagerHeader = "Skua Manager…";

    public static Menu InWindow(MainMenuViewModel viewModel, AvaloniaWindowService windows, Action? openManager = null)
    {
        Menu menu = new();
        foreach (MainMenuItemViewModel item in Items(viewModel))
            menu.Items.Add(MenuItem(item, windows));
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
        foreach (MainMenuItemViewModel item in Items(viewModel))
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
        if (openManager is not null)
            bar.Items.Add(new NativeMenuItem("Manager") { Menu = new NativeMenu { Items = { ManagerItem(openManager) } } });
        return bar;
    }

    /// <summary>Core's items, then the Plugins group, which the WPF window shows as its own menu.</summary>
    private static IEnumerable<MainMenuItemViewModel> Items(MainMenuViewModel viewModel) =>
        viewModel.MainMenuItems.Append(new MainMenuItemViewModel("Plugins", viewModel.Plugins));

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
            menuItem.IsEnabled = windows.CanShow(item.Header);
        }
        return menuItem;
    }

    private static NativeMenuItem NativeItem(MainMenuItemViewModel item, string header, AvaloniaWindowService windows) => new(header)
    {
        Command = item.Command,
        IsEnabled = windows.CanShow(item.Header),
    };
}
