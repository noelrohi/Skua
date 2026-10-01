using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The Mac App's own items in the app menu, where macOS puts About: About, a release's Check for Updates… and Change Logs, which the Windows Manager shows as tabs, and the
/// GitHub sign-in. Each opens its window through the window service, once, as a managed window; its view model is made on the first click,
/// since About and Change Logs fetch their pages as they are made.
/// </summary>
public static class AppMenu
{
    public const string AboutHeader = "About Skua";
    public const string ChangeLogsHeader = "Change Logs";
    public const string GitHubHeader = "GitHub Login…";

    /// <summary>The managed windows' keys.</summary>
    public const string AboutKey = "About";
    public const string ChangeLogsKey = "Change Logs";
    public const string GitHubKey = "GitHub Login";

    /// <param name="checkForUpdates">A release's Check for Updates…, after About; a dev build has none.</param>
    public static NativeMenu Create(IServiceProvider services, AvaloniaWindowService windows, Action? checkForUpdates = null)
    {
        NativeMenu menu = new()
        {
            Items =
            {
                Item<AboutViewModel>(AboutHeader, AboutKey, services, windows),
                Item<ChangeLogsViewModel>(ChangeLogsHeader, ChangeLogsKey, services, windows),
                new NativeMenuItemSeparator(),
                Item<GitHubAuthViewModel>(GitHubHeader, GitHubKey, services, windows),
            },
        };
        if (checkForUpdates is not null)
            menu.Items.Insert(1, new NativeMenuItem(AppUpdates.CheckHeader) { Command = new RelayCommand(checkForUpdates) });
        return menu;
    }

    /// <summary>Opens Change Logs as its menu item does; the app also opens it at its first start (<see cref="StartUpChecks"/>).</summary>
    public static void ShowChangeLogs(IServiceProvider services, AvaloniaWindowService windows) =>
        Show<ChangeLogsViewModel>(ChangeLogsKey, services, windows);

    private static NativeMenuItem Item<TViewModel>(string header, string key, IServiceProvider services, AvaloniaWindowService windows)
        where TViewModel : class, IManagedWindow => new(header)
    {
        Command = new RelayCommand(() => Show<TViewModel>(key, services, windows)),
    };

    private static void Show<TViewModel>(string key, IServiceProvider services, AvaloniaWindowService windows)
        where TViewModel : class, IManagedWindow
    {
        windows.RegisterManagedWindow(key, services.GetRequiredService<TViewModel>());
        windows.ShowManagedWindow(key);
    }
}
