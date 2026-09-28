using Avalonia.Controls;
using Avalonia.Threading;
using Skua.Avalonia.Views.Helpers;

namespace Skua.Avalonia.Views;

/// <summary>
/// Sends a packet to the server (or, with Send to Client, to the game), and spams the list's packets in turn with a delay between them;
/// the list follows the packet being sent.
/// </summary>
/// <remarks>Ports <c>ListBoxScrollToSelectedIndexBehavior</c>, <c>TextBoxOnlyNumbersBehavior</c> and <c>TextBoxSelectAllBehavior</c>.</remarks>
public partial class PacketSpammerView : UserControl
{
    public PacketSpammerView()
    {
        InitializeComponent();
        ListInput.DigitsOnly(Delay);
        Packets.SelectionChanged += (_, _) =>
        {
            if (Packets.SelectedItem is { } item)
                Dispatcher.UIThread.Post(() => Packets.ScrollIntoView(item), DispatcherPriority.Background);
        };
        PacketText.GotFocus += (_, _) => Dispatcher.UIThread.Post(PacketText.SelectAll);
    }
}
