using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>CoreBots' general options, by type then by name as on Windows, and the ones whose name has the search text.</summary>
public partial class CBOptionsView : UserControl
{
    public CBOptionsView()
    {
        InitializeComponent();
        Options.ItemTemplate = CoreBotsOptionRows.Template;
        SearchBox.TextChanged += (_, _) => Refresh();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Refresh();
    }

    private void Refresh()
    {
        if (DataContext is not CBOptionsViewModel model)
        {
            Options.ItemsSource = null;
            return;
        }
        string search = SearchBox.Text ?? "";
        // The Windows view groups them by type, in the order the types first appear by name.
        Options.ItemsSource = model.Options
            .Where(o => string.IsNullOrWhiteSpace(search) || o.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Content, StringComparer.Ordinal)
            .GroupBy(o => o.DisplayType)
            .SelectMany(g => g)
            .ToList();
    }
}
