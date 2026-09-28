using Avalonia.Controls;
using Skua.Avalonia.Services;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The main menu, built from Core's <see cref="MainMenuViewModel"/> as the WPF main menu is: in the window, and in the macOS menu bar.
/// An item opens its managed window, and is disabled while that window has no view (<see cref="AvaloniaWindowService.CanShow"/>);
/// items with their own command, such as Bank and the plugins, stay disabled until their tickets.
/// </summary>
public static class MainMenus
{
    public static Menu InWindow(MainMenuViewModel viewModel, AvaloniaWindowService windows)
    {
        Menu menu = new();
        foreach (MainMenuItemViewModel item in Items(viewModel))
            menu.Items.Add(MenuItem(item, windows));
        return menu;
    }

    /// <summary>A menu bar menu per group; an item without a group gets a menu of its own, as a menu bar item can't act by itself.</summary>
    public static NativeMenu Native(MainMenuViewModel viewModel, AvaloniaWindowService windows)
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
