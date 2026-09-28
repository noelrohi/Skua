using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>The inventory's and the bank's items, to mark as junk, searched by ID, name or category as on Windows.</summary>
public partial class JunkItemsView : UserControl
{
    private readonly SearchFilter _filter;

    public JunkItemsView()
    {
        InitializeComponent();
        _filter = new SearchFilter(Items, SearchBox, (item, search) => item is JunkItemEntry entry
            ? entry.ID.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
            : item.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _filter.Source = (DataContext as JunkItemsViewModel)?.Items;
    }
}
