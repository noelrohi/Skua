using System.Diagnostics;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Skua.Avalonia.Tests;

/// <summary>Drives the headless UI: waits that keep the dispatcher and renderer running, and clicks as a user makes them.</summary>
public static class Ui
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The windows open now, so a wait that times out can say which were, such as a dialog nobody expected.</summary>
    private static readonly HashSet<Window> s_open = [];

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void TrackWindows()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => s_open.Add(window));
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => s_open.Remove(window));
    }

    public static async Task PumpUntilAsync(Func<bool> done, string what, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? Timeout;
        Stopwatch waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > limit)
                throw new TimeoutException($"Waited {limit.TotalSeconds} s for {what}. Open windows: {OpenWindows()}.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private static string OpenWindows() =>
        string.Join(", ", s_open.Select(w => $"'{w.Title}' ({(w.DataContext ?? w.Content)?.GetType().Name})"));

    /// <summary>Clicks an enabled button, running its command as a pointer click would.</summary>
    public static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"'{button.Content}' is disabled");
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
    }

    /// <summary>
    /// Clicks a control's middle with the pointer, as the developer does, once it is scrolled into view. Open tooltips close first: a
    /// headless window shows its popups inside itself, where one can cover the control.
    /// </summary>
    public static async Task ClickAsync(Window window, global::Avalonia.Controls.Control control)
    {
        control.BringIntoView();
        await PumpUntilAsync(() => control.IsEffectivelyVisible && control.Bounds.Width > 0, "the control on screen");
        foreach (global::Avalonia.Controls.Control tipped in window.GetVisualDescendants().OfType<global::Avalonia.Controls.Control>().Where(ToolTip.GetIsOpen))
            ToolTip.SetIsOpen(tipped, false);
        await PumpUntilAsync(() => Find<ToolTip>(window) is null, "the tooltips to close");
        await PumpUntilAsync(() => true, "a layout pass");
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await PumpUntilAsync(() => true, "a layout pass");
    }

    /// <summary>The first control of type <typeparamref name="T"/> in <paramref name="root"/>'s visual tree that satisfies <paramref name="match"/>.</summary>
    public static T? Find<T>(Visual root, Func<T, bool>? match = null) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => match?.Invoke(c) ?? true);

    /// <summary>The text a button shows: its content, or its visible text block's.</summary>
    public static string? Text(Button button) => button.Content as string
        ?? button.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsVisible)?.Text;
}
