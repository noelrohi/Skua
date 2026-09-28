using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;

namespace Skua.Avalonia;

/// <summary>
/// Tells a developer who isn't looking that a Script stopped or failed, or that the game relogged, as the Windows tray's balloons do. While
/// the main window is closed or minimised, or the app isn't frontmost, each posts a notification through the poster the Script Dialogs
/// use; while the main window is in front, nothing is posted.
/// </summary>
/// <remarks>A Script that throws posts "Script Error" only: the stop that follows it isn't posted again.</remarks>
public sealed class ScriptStatusAlerts : IDisposable
{
    private readonly Window _main;
    private readonly Func<bool> _frontmost;
    private readonly Func<string> _username;
    private readonly Action<string, string> _notify;
    private int _errored;

    /// <param name="main">The main window.</param>
    /// <param name="frontmost">Whether the app is frontmost: one of its windows is active.</param>
    /// <param name="username">The logged-in account's username, read on the thread that relogs.</param>
    /// <param name="notify">Posts a notification with a title and a body.</param>
    public ScriptStatusAlerts(Window main, Func<bool> frontmost, Func<string> username, Action<string, string> notify)
    {
        _main = main;
        _frontmost = frontmost;
        _username = username;
        _notify = notify;
        // Core sends these on the Script's thread and the relogin's.
        IMessenger messenger = StrongReferenceMessenger.Default;
        int status = (int)MessageChannels.ScriptStatus;
        messenger.Register<ScriptStatusAlerts, ScriptErrorMessage, int>(this, status, static (r, m) => r.OnError(m.Exception));
        messenger.Register<ScriptStatusAlerts, ScriptStoppedMessage, int>(this, status, static (r, _) => r.OnStopped());
        messenger.Register<ScriptStatusAlerts, ReloginTriggeredMessage, int>(this, (int)MessageChannels.GameEvents, static (r, _) => r.OnRelogin());
    }

    public void Dispose() => StrongReferenceMessenger.Default.UnregisterAll(this);

    private void OnError(Exception e)
    {
        Interlocked.Exchange(ref _errored, 1);
        Exception thrown = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
        Post("Script Error", ScriptDialogAlerts.Body(string.Empty, thrown.Message));
    }

    private void OnStopped()
    {
        if (Interlocked.Exchange(ref _errored, 0) == 0)
            Post("Script Stopped", string.Empty);
    }

    private void OnRelogin() => Post("Relogin", $"Relogin triggered for {_username()}.");

    /// <summary>Decides on the UI thread, where the window's state can be read.</summary>
    private void Post(string title, string body) => Dispatcher.UIThread.Post(() =>
    {
        if (!_main.IsVisible || _main.WindowState == WindowState.Minimized || !_frontmost())
            _notify(title, body);
    });
}
