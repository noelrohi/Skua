using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Helpers;

public partial class ToPickupDropsView : UserControl
{
    public ToPickupDropsView()
    {
        InitializeComponent();
        ListInput.RemoveOnDelete(ToPickupList, () => Model?.RemoveDropsCommand, () => Model?.RemoveAllDropsCommand);
        ListInput.DigitsOnly(Interval);
    }

    private ToPickupDropsViewModel? Model => DataContext as ToPickupDropsViewModel;
}
