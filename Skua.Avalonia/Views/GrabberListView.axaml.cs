using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// One Grabber tab: grabs its items from the game, searched as the Windows view searches them, and shows the selected one's properties;
/// its tasks run on the selected items.
/// </summary>
public partial class GrabberListView : UserControl
{
    public static readonly DirectProperty<GrabberListView, IList<object>> SelectedItemsProperty =
        AvaloniaProperty.RegisterDirect<GrabberListView, IList<object>>(nameof(SelectedItems), v => v.SelectedItems);

    /// <summary>An item's text, marked when the Junk Items list holds it, as <c>GrabberItemDisplayConverter</c> writes it.</summary>
    public static readonly IValueConverter ItemText = new FuncValueConverter<object?, string>(item => item switch
    {
        ItemBase i when Ioc.Default.GetService<IJunkService>()?.IsJunk(i.ID) == true => $"{i} [Junk]",
        _ => item?.ToString() ?? "",
    });

    private readonly SearchFilter _filter;
    private IList<object> _selectedItems = [];
    private GrabberListViewModel? _viewModel;

    public GrabberListView()
    {
        InitializeComponent();
        _filter = new SearchFilter(Items, SearchBox, (item, search) => item.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
        Items.SelectionChanged += (_, _) => SetAndRaise(SelectedItemsProperty, ref _selectedItems, [.. Items.SelectedItems?.Cast<object>() ?? []]);
        SelectMultiple.IsCheckedChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.SelectionMode = SelectMultiple.IsChecked == true ? 1 : 0;
            ShowSelectionMode();
        };
        UnselectAll.Click += (_, _) => Items.UnselectAll();
        UnselectAllItem.Click += (_, _) => Items.UnselectAll();
    }

    /// <summary>The items selected in the list, for the tasks' commands.</summary>
    public IList<object> SelectedItems => _selectedItems;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as GrabberListViewModel;
        _filter.Source = _viewModel?.GrabbedItems;
        SelectMultiple.IsChecked = _viewModel?.SelectionMode == 1;
        ShowSelectionMode();
    }

    private void ShowSelectionMode() =>
        Items.SelectionMode = _viewModel?.SelectionMode == 1 ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single;
}
