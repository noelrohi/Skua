using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The Packet Interceptor: Connect reconnects the game to the selected server through Core's capture proxy on this Mac, and the list shows
/// each packet it relays, coloured by direction. The list is filtered by the search and by the unchecked filters, as the WPF view's code
/// filters it; the filters are searched by name. ⌘C copies the selected packets.
/// </summary>
/// <remarks>
/// The proxy listens on 127.0.0.1 at the server's port and the game connects to it with its own <c>connectTo</c>; the Game Host's sockets
/// are plain TCP (Ruffle's <c>SocketMode::Allow</c>), so this works as on Windows. Core adds packets on the UI thread, through the
/// synchronization context its view model was made on (<see cref="Services.UiThreadContext"/>). Connecting waits for the world to load,
/// so Connect runs off the UI thread and the window keeps drawing the packets meanwhile; it ends once the game is in the world, or with the
/// proxy stopped and the reason shown under the button if the game didn't get there within the Login Timeout.
/// </remarks>
public partial class PacketInterceptorView : UserControl
{
    public static readonly IImmutableSolidColorBrush OutboundBrush = new ImmutableSolidColorBrush(Colors.Yellow, 0.2);
    public static readonly IImmutableSolidColorBrush InboundBrush = new ImmutableSolidColorBrush(Colors.Blue, 0.2);
    public static readonly IImmutableSolidColorBrush BlockedBrush = new ImmutableSolidColorBrush(Colors.Red, 0.2);

    /// <summary>A packet's row colour: outbound, inbound, or blocked (no direction).</summary>
    public static readonly IValueConverter DirectionBrushes = new FuncValueConverter<bool?, IBrush>(outbound => outbound switch
    {
        true => OutboundBrush,
        false => InboundBrush,
        null => BlockedBrush,
    });

    private readonly FollowingList _packets;
    private readonly SearchFilter _filters;
    private PacketInterceptorViewModel? _viewModel;

    public PacketInterceptorView()
    {
        InitializeComponent();
        _packets = new FollowingList(Packets, Keep);
        SearchBox.TextChanged += (_, _) => _packets.Refresh();
        _filters = new SearchFilter(Filters, FilterSearchBox, (filter, search) => ((PacketLogFilterViewModel)filter).Content.Contains(search, StringComparison.OrdinalIgnoreCase));
        FollowingList.CopyAndUnselect(Packets);
        Connect.Click += OnConnectClick;
    }

    /// <summary>The packets the list shows.</summary>
    public IReadOnlyList<object> Shown => _packets.Shown;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            foreach (PacketLogFilterViewModel filter in _viewModel.PacketFilters)
                filter.PropertyChanged -= OnFilterChanged;
        }
        _viewModel = DataContext as PacketInterceptorViewModel;
        if (_viewModel is not null)
        {
            foreach (PacketLogFilterViewModel filter in _viewModel.PacketFilters)
                filter.PropertyChanged += OnFilterChanged;
        }
        _packets.Source = _viewModel?.Packets;
        _filters.Source = _viewModel?.PacketFilters;
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketLogFilterViewModel.IsChecked))
            UiThread.Post(_packets.Refresh);
    }

    /// <summary>Whether the list shows a packet: it has the search's text, and no unchecked filter matches it.</summary>
    private bool Keep(object item)
    {
        if (item is not InterceptedPacketViewModel packet)
            return false;
        if (SearchBox.Text is { Length: > 0 } search && !packet.Packet.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = [packet.Packet];
        return _viewModel?.PacketFilters.All(f => f.IsChecked || !f.Filter(parts)) ?? true;
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
            return;
        Connect.IsEnabled = false;
        try
        {
            await Task.Run(() => viewModel.ConnectInterceptorCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            // A server name that doesn't resolve, or a port the proxy can't take, leaves it disconnected rather than taking the app down.
            Trace.WriteLine($"The Packet Interceptor couldn't connect: {ex.Message}");
        }
        finally
        {
            Connect.IsEnabled = true;
        }
    }
}
