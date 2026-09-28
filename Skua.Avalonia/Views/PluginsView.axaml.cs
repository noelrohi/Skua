using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>The loaded plugins, searched by name or author.</summary>
public partial class PluginsView : UserControl
{
    private readonly SearchFilter _filter;

    public PluginsView()
    {
        InitializeComponent();
        _filter = new SearchFilter(Plugins, SearchBox, (item, search) => item is PluginItemViewModel plugin
            && (plugin.Container.Plugin.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                || plugin.Container.Plugin.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _filter.Source = (DataContext as PluginsViewModel)?.Plugins;
    }
}
