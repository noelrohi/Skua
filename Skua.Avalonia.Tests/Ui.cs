using System.Diagnostics;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Skua.Avalonia.Tests;

/// <summary>Drives the headless UI: waits that keep the dispatcher and renderer running, and clicks as a user makes them.</summary>
public static class Ui
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task PumpUntilAsync(Func<bool> done, string what, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? Timeout;
        Stopwatch waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > limit)
                throw new TimeoutException($"Waited {limit.TotalSeconds} s for {what}.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>Clicks an enabled button, running its command as a pointer click would.</summary>
    public static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"'{button.Content}' is disabled");
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
    }

    /// <summary>The first control of type <typeparamref name="T"/> in <paramref name="root"/>'s visual tree that satisfies <paramref name="match"/>.</summary>
    public static T? Find<T>(Visual root, Func<T, bool>? match = null) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => match?.Invoke(c) ?? true);

    /// <summary>The text a button shows: its content, or its visible text block's.</summary>
    public static string? Text(Button button) => button.Content as string
        ?? button.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsVisible)?.Text;
}
