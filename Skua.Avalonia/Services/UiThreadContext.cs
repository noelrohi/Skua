using Avalonia.Threading;

namespace Skua.Avalonia.Services;

/// <summary>
/// A synchronization context for Core's view models that capture the one they are made on: it runs work on Avalonia's UI thread, as the
/// dispatcher is when the work comes, from any thread. <see cref="Send"/> doesn't wait for the work, so a thread that hands the UI a
/// result, such as the Packet Interceptor's proxy relaying the game's traffic, never stalls on a busy UI thread; the work still runs in
/// the order it was sent.
/// </summary>
internal sealed class UiThreadContext : SynchronizationContext
{
    /// <summary>Runs <paramref name="make"/> with this context as the current one, and returns what it makes.</summary>
    public static T Run<T>(Func<T> make)
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(new UiThreadContext());
        try
        {
            return make();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public override void Post(SendOrPostCallback d, object? state) => Dispatcher.UIThread.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

    public override SynchronizationContext CreateCopy() => this;
}
