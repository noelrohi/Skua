using System.Collections.Specialized;
using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>The saved travels, filtered by the search; each joins its map, and Edit opens the edit dialog.</summary>
/// <remarks>Ports the WPF control's filtered collection view. Core changes the travels on the UI thread, from the panel's own commands.</remarks>
public partial class FastTravelView : UserControl
{
    private FastTravelViewModel? _model;

    public FastTravelView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Refill();
        ListInput.DigitsOnly(PrivateNumber);
    }

    /// <summary>The travels the list shows.</summary>
    public IReadOnlyList<FastTravelItemViewModel> Shown { get; private set; } = [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.FastTravelItems.CollectionChanged -= OnTravelsChanged;
        _model = DataContext as FastTravelViewModel;
        if (_model is not null)
            _model.FastTravelItems.CollectionChanged += OnTravelsChanged;
        Refill();
    }

    private void OnTravelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refill();

    private void Refill()
    {
        string search = SearchBox.Text ?? string.Empty;
        Shown = _model is null ? [] : _model.FastTravelItems.Where(t => t.DescriptionName.Contains(search)).ToList();
        Travels.ItemsSource = Shown;
    }
}
