using CommunityToolkit.Mvvm.ComponentModel;

namespace Skua.Avalonia;

/// <summary>
/// Keeps a panel active while any window shows it. The panels are singletons, so one can show in its own window and in the Bot Window at
/// once; either one closing, or the Bot Window moving on, would otherwise deactivate it under the other, which then stops updating.
/// </summary>
/// <remarks>UI thread only.</remarks>
internal static class PanelActivity
{
    private static readonly Dictionary<ObservableRecipient, int> s_shown = new(ReferenceEqualityComparer.Instance);

    /// <summary>A window shows <paramref name="panel"/>: it starts its work, as on Windows once its window shows.</summary>
    public static void Shown(ObservableRecipient panel)
    {
        s_shown[panel] = s_shown.GetValueOrDefault(panel) + 1;
        panel.IsActive = true;
    }

    /// <summary>A window no longer shows <paramref name="panel"/>: it stops its work once no window does.</summary>
    public static void Hidden(ObservableRecipient panel)
    {
        int count = s_shown.GetValueOrDefault(panel) - 1;
        if (count > 0)
            s_shown[panel] = count;
        else
            s_shown.Remove(panel);
        panel.IsActive = count > 0;
    }

    /// <summary>Makes <paramref name="panel"/> active again if a window still shows it, after something else deactivated it.</summary>
    public static void Restore(ObservableRecipient panel)
    {
        if (s_shown.ContainsKey(panel))
            panel.IsActive = true;
    }
}
