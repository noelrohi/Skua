using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The packets the game sends while logging is on, searched as the user types; the filters, which Core applies as packets arrive, are
/// searched by name. ⌘C copies the selected packets.
/// </summary>
/// <remarks>Core adds packets on the thread the game's <c>packet</c> call arrives on, so the list follows them on the UI thread.</remarks>
public partial class PacketLoggerView : UserControl
{
    private readonly FollowingList _packets;
    private readonly SearchFilter _filters;

    public PacketLoggerView()
    {
        InitializeComponent();
        _packets = new FollowingList(Packets, packet => SearchBox.Text is not { Length: > 0 } search
            || packet.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
        SearchBox.TextChanged += (_, _) => _packets.Refresh();
        _filters = new SearchFilter(Filters, FilterSearchBox, (filter, search) => ((PacketLogFilterViewModel)filter).Content.Contains(search, StringComparison.OrdinalIgnoreCase));
        FollowingList.CopyAndUnselect(Packets);
        UnselectAll.Click += (_, _) => Packets.UnselectAll();
    }

    /// <summary>The packets the list shows.</summary>
    public IReadOnlyList<object> Shown => _packets.Shown;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        PacketLoggerViewModel? viewModel = DataContext as PacketLoggerViewModel;
        _packets.Source = viewModel?.PacketLogs;
        _filters.Source = viewModel?.PacketFilters;
    }
}
