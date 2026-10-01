using Avalonia;
using Avalonia.Controls;

namespace Skua.Avalonia;

/// <summary>
/// The Mac App never takes focus from another app (#142). A window the app shows by itself, such as the options editor a Script opens as it
/// starts or restarts after a relogin, waits while another app is frontmost: a notification says so, and the window shows once the developer
/// brings Skua forward, with its Dock icon or by switching to it. Showing a window on macOS activates the app, over whatever is in front.
/// </summary>
public sealed class Foreground
{
    private readonly List<TaskCompletionSource> _waiting = [];

    public Foreground()
    {
        // Any of the app's windows becoming active means the app came forward.
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>((window, _) =>
        {
            if (window.IsActive)
                BroughtForward();
        });
    }

    /// <summary>Whether the app is frontmost; the app sets it to whether one of its windows is active. Until then, it always is.</summary>
    public Func<bool> Frontmost { get; set; } = () => true;

    /// <summary>Posts a notification with a title and a body; the app sets it to the macOS poster.</summary>
    public Action<string, string> Notify { get; set; } = (_, _) => { };

    public bool IsFrontmost => Frontmost();

    /// <summary>
    /// Completes at once while the app is frontmost. Otherwise posts <paramref name="title"/> and <paramref name="body"/>, and completes once
    /// the app comes forward, or is cancelled with <paramref name="cancellationToken"/>. Called on the UI thread.
    /// </summary>
    public Task UntilFrontmostAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        if (Frontmost())
            return Task.CompletedTask;
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting)
            _waiting.Add(waiting);
        cancellationToken.Register(() => waiting.TrySetCanceled(cancellationToken));
        Notify(title, body);
        return waiting.Task;
    }

    /// <summary>Lets what waits for the app to come forward show, if it is frontmost now.</summary>
    public void BroughtForward()
    {
        if (!Frontmost())
            return;
        TaskCompletionSource[] waiting;
        lock (_waiting)
        {
            waiting = [.. _waiting];
            _waiting.Clear();
        }
        foreach (TaskCompletionSource w in waiting)
            w.TrySetResult();
    }
}
