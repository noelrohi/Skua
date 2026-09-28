using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.Core.Models.Items;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

/// <summary>The drops the game offers, filtered by the search as the user types (Return searches at once); Pickup Selected picks the selected ones.</summary>
/// <remarks>
/// Ports the WPF control's filtered collection view and its search debounce. Core says the drops changed on the thread the game event
/// arrives on, so the list is refilled on the UI thread.
/// </remarks>
public partial class CurrentDropsView : UserControl
{
    private readonly DispatcherTimer _search = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CurrentDropsViewModel? _model;

    public CurrentDropsView()
    {
        InitializeComponent();
        _search.Tick += (_, _) => Refill();
        SearchBox.TextChanged += (_, _) =>
        {
            _search.Stop();
            _search.Start();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Refill();
                e.Handled = true;
            }
        };
        PickupSelected.Click += (_, _) => _model?.PickupSelectedCommand.Execute(ListInput.Selected(CurrentDropsList));
    }

    /// <summary>The drops the list shows.</summary>
    public IReadOnlyList<ItemBase> Shown { get; private set; } = [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as CurrentDropsViewModel;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
        Refill();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurrentDropsViewModel.CurrentDrops))
            Dispatcher.UIThread.Post(Refill);
    }

    private void Refill()
    {
        _search.Stop();
        string search = SearchBox.Text ?? string.Empty;
        Shown = _model is null
            ? []
            : _model.CurrentDrops.Where(d => search.Length == 0 || d.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        CurrentDropsList.ItemsSource = Shown;
    }
}
