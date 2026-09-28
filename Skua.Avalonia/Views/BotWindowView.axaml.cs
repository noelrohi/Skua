using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Threading;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The Bot Window: every panel in one window. The search keeps the panels whose title has the text, as <c>BotWindow.xaml.cs</c>'s filter
/// does, though ignoring case as the Mac App's other searches do. Home, Previous and Next move through every panel, filtered or not.
/// </summary>
/// <remarks>
/// WPF's list binds both <c>SelectedIndex</c> and <c>SelectedItem</c>, which keeps the two in step. Here the view keeps them in step, so the
/// list can show a filtered copy while the view model's index stays one into every panel. The panel shown counts as shown in a window
/// (<see cref="PanelActivity"/>) until the view leaves its window, so moving on or closing leaves it active if its own window shows it.
/// </remarks>
public partial class BotWindowView : UserControl
{
    private readonly SearchFilter _filter;
    private BotWindowViewModel? _model;
    private BotControlViewModelBase? _shown;
    private bool _showingSelection;

    public BotWindowView()
    {
        InitializeComponent();
        // Each panel by its title. In code, as a template typed in XAML for the panels' base class would read as a view for it.
        ViewsList.ItemTemplate = new FuncDataTemplate<BotControlViewModelBase>((_, _) =>
            new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(BotControlViewModelBase.Title)) });
        _filter = new SearchFilter(ViewsList, SearchBox, (item, search) =>
            item is BotControlViewModelBase panel && panel.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        // After the filter's own handler, which makes the list again.
        SearchBox.TextChanged += (_, _) => ShowSelection();
        ViewsList.SelectionChanged += (_, _) =>
        {
            if (!_showingSelection && _model is not null && ViewsList.SelectedItem is BotControlViewModelBase panel)
                _model.SelectedItem = panel;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Listen();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Listen();
    }

    /// <summary>Follows the view model while the view is in a window, and lets go of it, and of the panel shown, once it isn't.</summary>
    private void Listen()
    {
        BotWindowViewModel? model = VisualRoot is not null ? DataContext as BotWindowViewModel : null;
        if (ReferenceEquals(model, _model))
            return;
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = model;
        _filter.Source = model?.BotViews;
        if (model is not null)
            model.PropertyChanged += OnModelChanged;
        Show(model?.SelectedItem);
        ShowSelection();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_model is not { } model)
            return;
        switch (e.PropertyName)
        {
            case nameof(BotWindowViewModel.SelectedIndex):
                if (model.SelectedIndex >= 0 && model.SelectedIndex < model.BotViews.Count)
                    model.SelectedItem = model.BotViews[model.SelectedIndex];
                break;
            case nameof(BotWindowViewModel.SelectedItem):
                BotControlViewModelBase? previous = _shown;
                Show(model.SelectedItem);
                // The view model deactivates the panel it leaves once this returns; its own window may still show it.
                if (previous is not null)
                    Dispatcher.UIThread.Post(() => PanelActivity.Restore(previous));
                if (model.BotViews.IndexOf(model.SelectedItem) is int index and >= 0)
                    model.SelectedIndex = index;
                ShowSelection();
                break;
        }
    }

    private void Show(BotControlViewModelBase? panel)
    {
        if (ReferenceEquals(panel, _shown))
            return;
        if (_shown is not null)
            PanelActivity.Hidden(_shown);
        _shown = panel;
        if (panel is not null)
            PanelActivity.Shown(panel);
    }

    /// <summary>Selects the panel shown in the list, or nothing while the search hides it.</summary>
    private void ShowSelection()
    {
        _showingSelection = true;
        try
        {
            ViewsList.SelectedItem = _shown is not null && ViewsList.Items.Contains(_shown) ? _shown : null;
        }
        finally
        {
            _showingSelection = false;
        }
    }
}
