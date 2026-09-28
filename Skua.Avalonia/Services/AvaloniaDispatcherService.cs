using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's dispatcher on Avalonia's UI thread. Unlike WPF's, a call from another thread is queued rather than waited for: a Script or
/// Engine thread may hold a lock the UI thread is waiting on, and Core only uses it for updates it doesn't wait on.
/// </summary>
public sealed class AvaloniaDispatcherService : IDispatcherService
{
    public void Invoke(Action action) => UiThread.Post(action);
}
