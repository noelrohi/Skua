using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Control;
using Skua.Core.ViewModels;
using Skua.Engine;

namespace Skua.Avalonia.Views;

/// <summary>
/// The Script Source's Scripts, searched as the Windows view searches them (<see cref="ScriptRepoSearch"/>).
/// </summary>
/// <remarks>
/// The list shows a copy of the view model's Scripts made on the UI thread. Core refills them from other threads, and Avalonia replays
/// such changes on the UI thread later, by when the Scripts may have changed again (WPF synchronizes the collection instead).
/// Update runs <c>skua scripts update</c>'s operation on the Engine, which refuses while a Script runs.
/// </remarks>
public partial class ScriptRepoView : UserControl
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _searchDebounce;
    private ScriptRepoViewModel? _viewModel;
    private INotifyCollectionChanged? _scripts;
    private int _refreshQueued;

    public ScriptRepoView()
    {
        InitializeComponent();
        _searchDebounce = new DispatcherTimer { Interval = SearchDebounce };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Refresh();
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        SearchScope.SelectionChanged += (_, _) => Refresh();
    }

    /// <summary>The Scripts the list shows, in order.</summary>
    public IReadOnlyList<ScriptInfoViewModel> Shown => ScriptsList.ItemsSource as IReadOnlyList<ScriptInfoViewModel> ?? [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as ScriptRepoViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
        WatchScripts();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _searchDebounce.Stop();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptRepoViewModel.Scripts))
            UiThread.Post(WatchScripts);
    }

    private void WatchScripts()
    {
        if (_scripts is not null)
            _scripts.CollectionChanged -= OnScriptsChanged;
        _scripts = _viewModel?.Scripts;
        if (_scripts is not null)
            _scripts.CollectionChanged += OnScriptsChanged;
        Refresh();
    }

    /// <summary>Raised on whichever thread Core changes the Scripts on; one refresh is queued for however many changes.</summary>
    private void OnScriptsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        Volatile.Write(ref _refreshQueued, 0);
        if (_viewModel is null)
        {
            ScriptsList.ItemsSource = null;
            return;
        }
        if (Snapshot(_viewModel) is not { } scripts)
        {
            // Core is still changing them; its next change queues another refresh.
            return;
        }

        ScriptInfoViewModel? selected = _viewModel.SelectedItem;
        IReadOnlyList<ScriptInfoViewModel> shown = ScriptRepoSearch.Apply(scripts, SearchBox.Text ?? "", (ScriptSearchScope)Math.Max(0, SearchScope.SelectedIndex));
        ScriptsList.ItemsSource = shown;
        if (selected is not null && shown.Contains(selected))
            ScriptsList.SelectedItem = selected;
    }

    /// <summary>The view model's Scripts, or null while another thread is changing them.</summary>
    private static ScriptInfoViewModel[]? Snapshot(ScriptRepoViewModel viewModel)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return [.. viewModel.Scripts];
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
            }
        }
        return null;
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        UpdateScripts.IsEnabled = false;
        UpdateResult.Text = "Updating the Scripts…";
        try
        {
            ScriptsUpdateResult result = await Ioc.Default.GetRequiredService<EngineScripts>().UpdateAsync();
            UpdateResult.Text = Describe(result);
        }
        catch (Exception ex)
        {
            UpdateResult.Text = ex.Message;
        }
        finally
        {
            UpdateScripts.IsEnabled = true;
        }
        UpdateResult.SetValue(ToolTip.TipProperty, UpdateResult.Text);
        _viewModel?.RefreshScriptsCommand.Execute(null);
    }

    /// <summary>As <c>skua scripts update</c> says it, in one line.</summary>
    private static string Describe(ScriptsUpdateResult result)
    {
        string text = result.Mode switch
        {
            ScriptsUpdateMode.Full => $"Downloaded {result.Downloaded} Scripts (full download).",
            ScriptsUpdateMode.Incremental => $"Downloaded {result.Downloaded} changed Scripts.",
            _ => "The Scripts are up to date.",
        };
        return result.Failed.Count > 0 ? $"{text} {result.Failed.Count} failed to download; update again." : text;
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e) => OnSelected(sender, viewModel => viewModel.DownloadCommand.Execute(null));

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => OnSelected(sender, viewModel => viewModel.DeleteCommand.Execute(null));

    /// <summary>Runs a command of the view model on the Script whose context menu was used, as the Windows view selects it first.</summary>
    private void OnSelected(object? sender, Action<ScriptRepoViewModel> command)
    {
        if (_viewModel is null || (sender as StyledElement)?.DataContext is not ScriptInfoViewModel script)
            return;
        _viewModel.SelectedItem = script;
        command(_viewModel);
    }
}
