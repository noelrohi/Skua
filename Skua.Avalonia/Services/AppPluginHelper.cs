using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's plugin helper in the Mac App: a plugin's menu items join the Plugins menu on the UI thread, whichever thread adds them, and an
/// item whose action fails, such as one that opens a WPF window, logs the error instead of taking the app down.
/// </summary>
public sealed class AppPluginHelper : IPluginHelper
{
    private readonly ILogService _log;
    private readonly Dictionary<string, MainMenuItemViewModel> _buttons = [];

    public AppPluginHelper(ILogService log)
    {
        _log = log;
    }

    public void AddMenuButton(string text, Action action)
    {
        MainMenuItemViewModel item = new(text, new RelayCommand(() => Run(text, action)));
        lock (_buttons)
        {
            if (!_buttons.TryAdd(text, item))
                return;
        }
        UiThread.Post(() => StrongReferenceMessenger.Default.Send<AddPluginMenuItemMessage, int>(new(item), (int)MessageChannels.Plugins));
    }

    public void RemoveMenuButton(string text)
    {
        MainMenuItemViewModel? item;
        lock (_buttons)
        {
            if (!_buttons.Remove(text, out item))
                return;
        }
        UiThread.Post(() => StrongReferenceMessenger.Default.Send<RemovePluginMenuItemMessage, int>(new(item), (int)MessageChannels.Plugins));
    }

    private void Run(string text, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.DebugLog($"The plugin menu item '{text}' failed: {AppPluginManager.Describe(e)}");
        }
    }
}
