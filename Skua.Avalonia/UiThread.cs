using Avalonia.Threading;

namespace Skua.Avalonia;

/// <summary>Runs work on Avalonia's UI thread for Core's services, whose calls come from any thread and are synchronous.</summary>
internal static class UiThread
{
    /// <summary>Runs <paramref name="action"/> at once on the UI thread, else queues it there without waiting.</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Runs asynchronous UI work, such as a file picker, and waits for its result: on the UI thread in a nested frame, so the UI keeps
    /// running meanwhile, and from any other thread by blocking that thread only.
    /// </summary>
    public static T Wait<T>(Func<Task<T>> work)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(work).GetAwaiter().GetResult();

        Task<T> task = work();
        if (!task.IsCompleted)
        {
            DispatcherFrame frame = new();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
}
