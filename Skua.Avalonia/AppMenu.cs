using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia;

/// <summary>
/// The Mac App's own items in the app menu, where macOS puts About: About and Change Logs, which the Windows Manager shows as tabs, and the
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

    public static NativeMenu Create(IServiceProvider services, AvaloniaWindowService windows) => new()
    {
        Items =
        {
            Item<AboutViewModel>(AboutHeader, AboutKey, services, windows),
            Item<ChangeLogsViewModel>(ChangeLogsHeader, ChangeLogsKey, services, windows),
            new NativeMenuItemSeparator(),
            Item<GitHubAuthViewModel>(GitHubHeader, GitHubKey, services, windows),
        },
    };

    private static NativeMenuItem Item<TViewModel>(string header, string key, IServiceProvider services, AvaloniaWindowService windows)
        where TViewModel : class, IManagedWindow => new(header)
    {
        Command = new RelayCommand(() =>
        {
            windows.RegisterManagedWindow(key, services.GetRequiredService<TViewModel>());
            windows.ShowManagedWindow(key);
        }),
    };
}
