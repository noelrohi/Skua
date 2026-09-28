using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Avalonia.Services;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// The game options, sorted by kind then name as on Windows and filtered by the search box, in as many columns as asked. They apply to the
/// game at once, and each one changed here is saved as it changes (<see cref="GameOptionEdits"/>).
/// </summary>
public partial class GameOptionsView : UserControl
{
    private readonly GameOptionEdits? _edits = Ioc.Default.GetService<GameOptionEdits>();
    private GameOptionsViewModel? _model;
    private bool _showingServer;

    public GameOptionsView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => ShowOptions();
        ColumnsBox.TextChanged += (_, _) =>
        {
            if (_model is not null && int.TryParse(ColumnsBox.Text, out int columns) && columns > 0)
                _model.Columns = columns;
        };
        ServerPicker.SelectionChanged += (_, _) =>
        {
            if (!_showingServer && _model is { } model && ServerPicker.SelectedItem is string server && server != model.SelectedServer)
                Edit(() => model.SelectedServer = server);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as GameOptionsViewModel;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
        ColumnsBox.Text = _model?.Columns.ToString();
        ShowOptions();
        ShowServer();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => UiThread.Post(() =>
    {
        switch (e.PropertyName)
        {
            case nameof(GameOptionsViewModel.Columns):
                Options.Columns = Math.Max(1, _model?.Columns ?? 1);
                break;
            case nameof(GameOptionsViewModel.SelectedServer):
            case nameof(GameOptionsViewModel.ServersList):
                ShowServer();
                break;
        }
    });

    private void ShowOptions()
    {
        Options.Children.Clear();
        if (_model is null)
            return;
        Options.Columns = Math.Max(1, _model.Columns);
        string search = SearchBox.Text ?? "";
        IEnumerable<DisplayOptionItemViewModelBase> shown = _model.GameOptions
            .Where(o => search.Length == 0 || o.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Tag, StringComparer.Ordinal)
            .ThenBy(o => o.Content, StringComparer.CurrentCulture);
        foreach (DisplayOptionItemViewModelBase option in shown)
            Options.Children.Add(new OptionItemView(option, Edit));
    }

    private void ShowServer()
    {
        _showingServer = true;
        try
        {
            ServerPicker.SelectedItem = _model?.SelectedServer;
        }
        finally
        {
            _showingServer = false;
        }
    }

    private void Edit(Action edit)
    {
        if (_edits is not null)
            _edits.Edit(edit);
        else
            edit();
    }
}
